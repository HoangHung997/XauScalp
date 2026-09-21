#property strict
#property description "XauScalp market-data bridge only. It never submits or modifies orders."

input string InpOutputFile = "XauScalp\\mt5-wire-v1.ndjson";
input int InpFlushIntervalMs = 100;
input int InpNewsRefreshIntervalSec = 15;
input int InpNewsLookbackSec = 86400;
input int InpNewsLookaheadSec = 86400;
input string InpNewsCurrency = "USD";

int g_file = INVALID_HANDLE;
ulong g_sequence = 0;
bool g_last_connected = false;
ulong g_last_flush_ms = 0;
ulong g_last_news_ms = 0;

string SequenceVariableName()
{
   return "XauScalp.Sequence." + _Symbol;
}

string JsonEscape(string value)
{
   StringReplace(value, "\\", "\\\\");
   StringReplace(value, "\"", "\\\"");
   StringReplace(value, "\r", "\\r");
   StringReplace(value, "\n", "\\n");
   return value;
}

ulong NextSequence()
{
   g_sequence++;
   GlobalVariableSet(SequenceVariableName(), (double)g_sequence);
   return g_sequence;
}

void MaybeFlush(const bool force)
{
   if(g_file == INVALID_HANDLE)
      return;

   const ulong now_ms = GetTickCount64();
   if(force || (long)(now_ms - g_last_flush_ms) >= MathMax(InpFlushIntervalMs, 1))
   {
      FileFlush(g_file);
      g_last_flush_ms = now_ms;
   }
}

bool WriteFrame(const string json)
{
   if(g_file == INVALID_HANDLE)
      return false;

   const uint written = FileWriteString(g_file, json + "\r\n");
   if(written == 0)
   {
      PrintFormat("XauScalp bridge write failed. MQL5 error=%d", GetLastError());
      return false;
   }

   MaybeFlush(false);
   return true;
}

void EmitConnection(const string state, const string reason)
{
   const ulong sequence = NextSequence();
   const string json = StringFormat(
      "{\"type\":\"connection\",\"sequence\":%s,\"brokerSymbol\":\"%s\",\"state\":\"%s\",\"reason\":\"%s\"}",
      IntegerToString((long)sequence),
      JsonEscape(_Symbol),
      JsonEscape(state),
      JsonEscape(reason));

   WriteFrame(json);
}

void EmitSymbolSpecification()
{
   const ulong sequence = NextSequence();
   const int digits = (int)SymbolInfoInteger(_Symbol, SYMBOL_DIGITS);
   const double point = SymbolInfoDouble(_Symbol, SYMBOL_POINT);
   const double tick_size = SymbolInfoDouble(_Symbol, SYMBOL_TRADE_TICK_SIZE);
   const double tick_value = SymbolInfoDouble(_Symbol, SYMBOL_TRADE_TICK_VALUE);
   const double contract_size = SymbolInfoDouble(_Symbol, SYMBOL_TRADE_CONTRACT_SIZE);
   const double min_volume = SymbolInfoDouble(_Symbol, SYMBOL_VOLUME_MIN);
   const double max_volume = SymbolInfoDouble(_Symbol, SYMBOL_VOLUME_MAX);
   const double volume_step = SymbolInfoDouble(_Symbol, SYMBOL_VOLUME_STEP);
   const long stops_level_points = SymbolInfoInteger(_Symbol, SYMBOL_TRADE_STOPS_LEVEL);
   const double min_stop_distance = stops_level_points * point;

   const string json = StringFormat(
      "{\"type\":\"symbol\",\"sequence\":%s,\"brokerSymbol\":\"%s\","
      "\"digits\":%d,\"point\":%s,\"tickSize\":%s,\"tickValue\":%s,"
      "\"contractSize\":%s,\"minVolume\":%s,\"maxVolume\":%s,"
      "\"volumeStep\":%s,\"minStopDistance\":%s}",
      IntegerToString((long)sequence),
      JsonEscape(_Symbol),
      digits,
      DoubleToString(point, 10),
      DoubleToString(tick_size, 10),
      DoubleToString(tick_value, 10),
      DoubleToString(contract_size, 8),
      DoubleToString(min_volume, 8),
      DoubleToString(max_volume, 8),
      DoubleToString(volume_step, 8),
      DoubleToString(min_stop_distance, 10));

   WriteFrame(json);
}

void EmitNewsUnavailable(const int error_code)
{
   const ulong sequence = NextSequence();
   const string source = "mt5-economic-calendar:" + InpNewsCurrency + ":high";
   const string json = StringFormat(
      "{\"type\":\"news\",\"sequence\":%s,\"brokerSymbol\":\"%s\","
      "\"available\":false,\"newsDistanceBeforeSec\":null,"
      "\"newsDistanceAfterSec\":null,\"source\":\"%s\","
      "\"sourceErrorCode\":%d}",
      IntegerToString((long)sequence),
      JsonEscape(_Symbol),
      JsonEscape(source),
      error_code);

   WriteFrame(json);
}

void EmitNewsContext()
{
   g_last_news_ms = GetTickCount64();

   const datetime server_now = TimeTradeServer();
   if(server_now <= 0)
   {
      EmitNewsUnavailable(0);
      return;
   }

   MqlCalendarValue values[];
   ResetLastError();

   const datetime date_from = server_now - InpNewsLookbackSec;
   const datetime date_to = server_now + InpNewsLookaheadSec;

   const int count = CalendarValueHistory(
      values,
      date_from,
      date_to,
      NULL,
      InpNewsCurrency);

   if(count < 0)
   {
      EmitNewsUnavailable(GetLastError());
      return;
   }

   long distance_before = (long)InpNewsLookaheadSec + 1;
   long distance_after = (long)InpNewsLookbackSec + 1;

   for(int i = 0; i < count; i++)
   {
      MqlCalendarEvent event;
      ResetLastError();

      if(!CalendarEventById(values[i].event_id, event))
      {
         EmitNewsUnavailable(GetLastError());
         return;
      }

      if(event.importance != CALENDAR_IMPORTANCE_HIGH)
         continue;

      const long delta = (long)values[i].time - (long)server_now;

      if(delta >= 0 && delta < distance_before)
         distance_before = delta;

      if(delta <= 0 && -delta < distance_after)
         distance_after = -delta;
   }

   const ulong sequence = NextSequence();
   const string source = "mt5-economic-calendar:" + InpNewsCurrency + ":high";
   const string json = StringFormat(
      "{\"type\":\"news\",\"sequence\":%s,\"brokerSymbol\":\"%s\","
      "\"available\":true,\"newsDistanceBeforeSec\":%s,"
      "\"newsDistanceAfterSec\":%s,\"source\":\"%s\","
      "\"sourceErrorCode\":null}",
      IntegerToString((long)sequence),
      JsonEscape(_Symbol),
      IntegerToString(distance_before),
      IntegerToString(distance_after),
      JsonEscape(source));

   WriteFrame(json);
}

int OnInit()
{
   if(InpFlushIntervalMs <= 0
      || InpNewsRefreshIntervalSec <= 0
      || InpNewsLookbackSec <= 0
      || InpNewsLookaheadSec <= 0
      || StringLen(InpNewsCurrency) == 0)
   {
      Print("XauScalp bridge inputs must be positive and news currency must be set.");
      return INIT_PARAMETERS_INCORRECT;
   }

   FolderCreate("XauScalp", FILE_COMMON);

   g_file = FileOpen(
      InpOutputFile,
      FILE_READ | FILE_WRITE | FILE_TXT | FILE_ANSI | FILE_COMMON | FILE_SHARE_READ | FILE_SHARE_WRITE);

   if(g_file == INVALID_HANDLE)
   {
      PrintFormat("Cannot open XauScalp bridge file '%s'. MQL5 error=%d", InpOutputFile, GetLastError());
      return INIT_FAILED;
   }

   FileSeek(g_file, 0, SEEK_END);

   const string sequence_variable = SequenceVariableName();
   if(GlobalVariableCheck(sequence_variable))
      g_sequence = (ulong)GlobalVariableGet(sequence_variable);

   g_last_connected = (bool)TerminalInfoInteger(TERMINAL_CONNECTED);

   EmitSymbolSpecification();
   EmitConnection(g_last_connected ? "connected" : "disconnected", "OnInit");
   EmitNewsContext();
   MaybeFlush(true);

   if(!EventSetMillisecondTimer(250))
   {
      PrintFormat("Cannot start XauScalp connection timer. MQL5 error=%d", GetLastError());
      FileClose(g_file);
      g_file = INVALID_HANDLE;
      return INIT_FAILED;
   }

   return INIT_SUCCEEDED;
}

void OnDeinit(const int reason)
{
   EventKillTimer();

   if(g_file != INVALID_HANDLE)
   {
      EmitConnection("disconnected", "EA deinitialized");
      MaybeFlush(true);
      FileClose(g_file);
      g_file = INVALID_HANDLE;
   }
}

void OnTimer()
{
   const bool connected = (bool)TerminalInfoInteger(TERMINAL_CONNECTED);
   bool force_flush = false;

   if(connected != g_last_connected)
   {
      if(connected)
      {
         EmitConnection("connected", "terminal reconnected");
         EmitSymbolSpecification();
      }
      else
      {
         EmitConnection("disconnected", "terminal disconnected");
      }

      g_last_connected = connected;
      force_flush = true;
   }

   const ulong now_ms = GetTickCount64();
   const ulong news_interval_ms =
      (ulong)InpNewsRefreshIntervalSec * 1000;

   if(now_ms - g_last_news_ms >= news_interval_ms)
   {
      EmitNewsContext();
      force_flush = true;
   }

   if(force_flush)
      MaybeFlush(true);
}

void OnTick()
{
   if(g_file == INVALID_HANDLE)
      return;

   MqlTick tick;
   if(!SymbolInfoTick(_Symbol, tick))
      return;

   const ulong sequence = NextSequence();
   const string json = StringFormat(
      "{\"type\":\"tick\",\"sequence\":%s,\"brokerSymbol\":\"%s\","
      "\"brokerTimeMsc\":%s,\"bid\":%s,\"ask\":%s,\"last\":%s,"
      "\"volume\":%s,\"flags\":%d}",
      IntegerToString((long)sequence),
      JsonEscape(_Symbol),
      IntegerToString(tick.time_msc),
      DoubleToString(tick.bid, 10),
      DoubleToString(tick.ask, 10),
      DoubleToString(tick.last, 10),
      DoubleToString(tick.volume_real, 8),
      (int)tick.flags);

   WriteFrame(json);
}
