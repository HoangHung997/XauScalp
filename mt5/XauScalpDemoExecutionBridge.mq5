#property strict
#property description "XauScalp DEMO-ONLY execution bridge. Refuses non-demo accounts."

#include <Trade/Trade.mqh>

input string InpCommandFile = "XauScalp\\mt5-demo-execution-commands-v1.ndjson";
input string InpEventFile = "XauScalp\\mt5-demo-execution-events-v1.ndjson";
input ulong InpMagicNumber = 991188;
input int InpPollIntervalMs = 100;
input int InpHeartbeatIntervalMs = 1000;

const string PROTOCOL_VERSION = "xau-mt5-demo-exec-v1";

CTrade g_trade;
int g_event_file = INVALID_HANDLE;
string g_session_id = "";
ulong g_last_heartbeat_ms = 0;
int g_command_lines_seen = 0;

string g_final_command_ids[];
string g_dispatching_command_ids[];
string g_mapping_comments[];
string g_mapping_trade_intents[];
long g_mapping_magic_numbers[];

string JsonEscape(string value)
{
   StringReplace(value, "\\", "\\\\");
   StringReplace(value, """, "\\"");
   StringReplace(value, "\r", "\\r");
   StringReplace(value, "\n", "\\n");
   StringReplace(value, "\t", "\\t");
   return value;
}

string JsonStringOrNull(const string value)
{
   if(StringLen(value) == 0)
      return "null";

   return """ + JsonEscape(value) + """;
}

string JsonNumberOrNull(const double value, const bool has_value, const int digits = 10)
{
   if(!has_value)
      return "null";

   return DoubleToString(value, digits);
}

int JsonValueStart(const string json, const string key)
{
   const string token = """ + key + """;
   int position = StringFind(json, token);
   if(position < 0)
      return -1;

   position = StringFind(json, ":", position + StringLen(token));
   if(position < 0)
      return -1;

   position++;
   while(position < StringLen(json))
   {
      const ushort ch = StringGetCharacter(json, position);
      if(ch != ' ' && ch != '\t' && ch != '\r' && ch != '\n')
         break;
      position++;
   }

   return position;
}

string JsonString(const string json, const string key)
{
   int start = JsonValueStart(json, key);
   if(start < 0 || start >= StringLen(json))
      return "";

   if(StringSubstr(json, start, 4) == "null")
      return "";

   if(StringGetCharacter(json, start) != '"')
      return "";

   const int end = StringFind(json, """, start + 1);
   if(end < 0)
      return "";

   return StringSubstr(json, start + 1, end - start - 1);
}

string JsonToken(const string json, const string key)
{
   int start = JsonValueStart(json, key);
   if(start < 0)
      return "";

   int end = start;
   while(end < StringLen(json))
   {
      const ushort ch = StringGetCharacter(json, end);
      if(ch == ',' || ch == '}' || ch == ']'
         || ch == ' ' || ch == '\t' || ch == '\r' || ch == '\n')
         break;
      end++;
   }

   return StringSubstr(json, start, end - start);
}

long JsonLong(const string json, const string key, const long fallback = 0)
{
   const string token = JsonToken(json, key);
   if(StringLen(token) == 0 || token == "null")
      return fallback;

   return StringToInteger(token);
}

double JsonDouble(const string json, const string key, const double fallback = 0.0)
{
   const string token = JsonToken(json, key);
   if(StringLen(token) == 0 || token == "null")
      return fallback;

   return StringToDouble(token);
}

bool JsonBool(const string json, const string key, const bool fallback = false)
{
   const string token = JsonToken(json, key);
   if(token == "true")
      return true;
   if(token == "false")
      return false;

   return fallback;
}

void AddUnique(string &items[], const string value)
{
   if(StringLen(value) == 0)
      return;

   const int count = ArraySize(items);
   for(int i = 0; i < count; i++)
   {
      if(items[i] == value)
         return;
   }

   ArrayResize(items, count + 1);
   items[count] = value;
}

bool ContainsString(string &items[], const string value)
{
   const int count = ArraySize(items);
   for(int i = 0; i < count; i++)
   {
      if(items[i] == value)
         return true;
   }

   return false;
}

void RegisterMapping(
   const string broker_comment,
   const string trade_intent_id,
   const long magic_number)
{
   if(StringLen(broker_comment) == 0 || StringLen(trade_intent_id) == 0)
      return;

   const int count = ArraySize(g_mapping_comments);
   for(int i = 0; i < count; i++)
   {
      if(g_mapping_comments[i] == broker_comment)
      {
         if(g_mapping_trade_intents[i] != trade_intent_id)
         {
            PrintFormat(
               "XauScalp demo bridge mapping collision for comment '%s'.",
               broker_comment);
         }
         return;
      }
   }

   ArrayResize(g_mapping_comments, count + 1);
   ArrayResize(g_mapping_trade_intents, count + 1);
   ArrayResize(g_mapping_magic_numbers, count + 1);

   g_mapping_comments[count] = broker_comment;
   g_mapping_trade_intents[count] = trade_intent_id;
   g_mapping_magic_numbers[count] = magic_number;
}

string TradeIntentForComment(const string broker_comment)
{
   const int count = ArraySize(g_mapping_comments);
   for(int i = 0; i < count; i++)
   {
      if(g_mapping_comments[i] == broker_comment)
         return g_mapping_trade_intents[i];
   }

   return "";
}

bool WriteEvent(const string json)
{
   if(g_event_file == INVALID_HANDLE)
      return false;

   FileSeek(g_event_file, 0, SEEK_END);
   const uint written = FileWriteString(g_event_file, json + "\r\n");
   if(written == 0)
   {
      PrintFormat(
         "XauScalp demo bridge event write failed. MQL5 error=%d",
         GetLastError());
      return false;
   }

   FileFlush(g_event_file);
   return true;
}

void EmitBridgeReady()
{
   const long utc_ms = ((long)TimeGMT()) * 1000;
   const string json = StringFormat(
      "{\"protocolVersion\":\"%s\",\"type\":\"bridgeReady\","
      "\"bridgeSessionId\":\"%s\",\"demoAccountVerified\":true,"
      "\"brokerSymbol\":\"%s\",\"magicNumber\":%s,\"utcUnixMs\":%s}",
      PROTOCOL_VERSION,
      JsonEscape(g_session_id),
      JsonEscape(_Symbol),
      IntegerToString((long)InpMagicNumber),
      IntegerToString(utc_ms));

   WriteEvent(json);
}

void EmitHeartbeat()
{
   const long utc_ms = ((long)TimeGMT()) * 1000;
   const string json = StringFormat(
      "{\"protocolVersion\":\"%s\",\"type\":\"bridgeHeartbeat\","
      "\"bridgeSessionId\":\"%s\",\"demoAccountVerified\":true,"
      "\"brokerSymbol\":\"%s\",\"magicNumber\":%s,\"utcUnixMs\":%s}",
      PROTOCOL_VERSION,
      JsonEscape(g_session_id),
      JsonEscape(_Symbol),
      IntegerToString((long)InpMagicNumber),
      IntegerToString(utc_ms));

   if(WriteEvent(json))
      g_last_heartbeat_ms = GetTickCount64();
}

void EmitDispatching(
   const string command_id,
   const string operation,
   const string trade_intent_id)
{
   const string json = StringFormat(
      "{\"protocolVersion\":\"%s\",\"type\":\"dispatching\","
      "\"commandId\":\"%s\",\"bridgeSessionId\":\"%s\","
      "\"demoAccountVerified\":true,\"operation\":\"%s\","
      "\"tradeIntentId\":%s}",
      PROTOCOL_VERSION,
      JsonEscape(command_id),
      JsonEscape(g_session_id),
      JsonEscape(operation),
      JsonStringOrNull(trade_intent_id));

   if(WriteEvent(json))
      AddUnique(g_dispatching_command_ids, command_id);
}

string OutcomeFromRetcode(const uint retcode)
{
   if(retcode == TRADE_RETCODE_DONE)
      return "filled";
   if(retcode == TRADE_RETCODE_DONE_PARTIAL)
      return "partiallyFilled";
   if(retcode == TRADE_RETCODE_PLACED)
      return "accepted";
   if(retcode == TRADE_RETCODE_REQUOTE
      || retcode == TRADE_RETCODE_PRICE_CHANGED)
      return "requote";

   return "rejected";
}

void EmitExecutionResult(
   const string command_id,
   const string operation,
   const string trade_intent_id,
   const string outcome,
   const string broker_order_id,
   const string broker_deal_id,
   const string broker_position_id,
   const double requested_price,
   const bool has_requested_price,
   const double fill_price,
   const bool has_fill_price,
   const double requested_volume,
   const double filled_volume,
   const double slippage_points,
   const bool has_slippage,
   const double latency_ms,
   const string broker_retcode,
   const string message,
   const bool safe_to_retry)
{
   const string json = StringFormat(
      "{\"protocolVersion\":\"%s\",\"type\":\"executionResult\","
      "\"commandId\":\"%s\",\"bridgeSessionId\":\"%s\","
      "\"demoAccountVerified\":true,\"operation\":\"%s\","
      "\"tradeIntentId\":%s,\"outcome\":\"%s\","
      "\"brokerOrderId\":%s,\"brokerDealId\":%s,"
      "\"brokerPositionId\":%s,\"requestedPrice\":%s,"
      "\"fillPrice\":%s,\"requestedVolumeLots\":%s,"
      "\"filledVolumeLots\":%s,\"slippagePoints\":%s,"
      "\"latencyMs\":%s,\"brokerRetcode\":%s,\"message\":%s,"
      "\"safeToRetry\":%s}",
      PROTOCOL_VERSION,
      JsonEscape(command_id),
      JsonEscape(g_session_id),
      JsonEscape(operation),
      JsonStringOrNull(trade_intent_id),
      JsonEscape(outcome),
      JsonStringOrNull(broker_order_id),
      JsonStringOrNull(broker_deal_id),
      JsonStringOrNull(broker_position_id),
      JsonNumberOrNull(requested_price, has_requested_price, 10),
      JsonNumberOrNull(fill_price, has_fill_price, 10),
      DoubleToString(requested_volume, 8),
      DoubleToString(filled_volume, 8),
      JsonNumberOrNull(slippage_points, has_slippage, 8),
      DoubleToString(latency_ms, 3),
      JsonStringOrNull(broker_retcode),
      JsonStringOrNull(message),
      safe_to_retry ? "true" : "false");

   if(WriteEvent(json))
      AddUnique(g_final_command_ids, command_id);
}

void EmitRejectedCommand(
   const string command_id,
   const string operation,
   const string trade_intent_id,
   const string retcode,
   const string message)
{
   EmitExecutionResult(
      command_id,
      operation,
      trade_intent_id,
      "rejected",
      "",
      "",
      "",
      0.0,
      false,
      0.0,
      false,
      0.0,
      0.0,
      0.0,
      false,
      0.0,
      retcode,
      message,
      false);
}

void EmitUnknownCommand(
   const string command_id,
   const string operation,
   const string trade_intent_id,
   const string message)
{
   EmitExecutionResult(
      command_id,
      operation,
      trade_intent_id,
      "unknown",
      "",
      "",
      "",
      0.0,
      false,
      0.0,
      false,
      0.0,
      0.0,
      0.0,
      false,
      0.0,
      "UNKNOWN_NEEDS_RECONCILIATION",
      message,
      false);
}

ulong FindPositionByComment(
   const string broker_comment,
   const long magic_number)
{
   const int total = PositionsTotal();
   for(int i = 0; i < total; i++)
   {
      const ulong ticket = PositionGetTicket(i);
      if(ticket == 0 || !PositionSelectByTicket(ticket))
         continue;

      if(PositionGetString(POSITION_SYMBOL) != _Symbol)
         continue;

      if((long)PositionGetInteger(POSITION_MAGIC) != magic_number)
         continue;

      if(PositionGetString(POSITION_COMMENT) == broker_comment)
         return ticket;
   }

   return 0;
}

ulong FindOrderByComment(
   const string broker_comment,
   const long magic_number)
{
   const int total = OrdersTotal();
   for(int i = 0; i < total; i++)
   {
      const ulong ticket = OrderGetTicket(i);
      if(ticket == 0)
         continue;

      if(OrderGetString(ORDER_SYMBOL) != _Symbol)
         continue;

      if((long)OrderGetInteger(ORDER_MAGIC) != magic_number)
         continue;

      if(OrderGetString(ORDER_COMMENT) == broker_comment)
         return ticket;
   }

   return 0;
}

bool HasHistoryEntry(
   const string broker_comment,
   const long magic_number)
{
   if(!HistorySelect(0, TimeCurrent()))
      return false;

   const int total = HistoryDealsTotal();
   for(int i = total - 1; i >= 0; i--)
   {
      const ulong deal = HistoryDealGetTicket(i);
      if(deal == 0)
         continue;

      if(HistoryDealGetString(deal, DEAL_SYMBOL) != _Symbol)
         continue;

      if((long)HistoryDealGetInteger(deal, DEAL_MAGIC) != magic_number)
         continue;

      const long entry = HistoryDealGetInteger(deal, DEAL_ENTRY);
      if(entry != DEAL_ENTRY_IN && entry != DEAL_ENTRY_INOUT)
         continue;

      if(HistoryDealGetString(deal, DEAL_COMMENT) == broker_comment)
         return true;
   }

   return false;
}

bool SelectOwnedPosition(
   const ulong ticket,
   const string broker_comment,
   const long magic_number)
{
   if(ticket == 0 || !PositionSelectByTicket(ticket))
      return false;

   if(PositionGetString(POSITION_SYMBOL) != _Symbol)
      return false;

   if((long)PositionGetInteger(POSITION_MAGIC) != magic_number)
      return false;

   if(StringLen(broker_comment) > 0
      && PositionGetString(POSITION_COMMENT) != broker_comment)
      return false;

   return true;
}

void ConfigureTrade(const long magic_number, const int deviation_points)
{
   g_trade.SetExpertMagicNumber((ulong)magic_number);
   g_trade.SetDeviationInPoints((ulong)MathMax(deviation_points, 0));
   g_trade.SetAsyncMode(false);
   g_trade.SetTypeFillingBySymbol(_Symbol);
}

void ExecuteSubmit(const string line)
{
   const string command_id = JsonString(line, "commandId");
   const string trade_intent_id = JsonString(line, "tradeIntentId");
   const string broker_comment = JsonString(line, "brokerComment");
   const string side = JsonString(line, "side");
   const long magic_number = JsonLong(line, "magicNumber", -1);
   const int deviation_points = (int)JsonLong(line, "maxSlippagePoints", 0);
   const double volume = JsonDouble(line, "volumeLots", 0.0);
   const double stop_loss = JsonDouble(line, "stopLossPrice", 0.0);
   const double take_profit = JsonDouble(line, "takeProfitPrice", 0.0);

   if(volume <= 0.0 || stop_loss <= 0.0)
   {
      EmitRejectedCommand(
         command_id,
         "submit",
         trade_intent_id,
         "INVALID_COMMAND",
         "Submit requires positive volume and protective stop.");
      return;
   }

   ConfigureTrade(magic_number, deviation_points);

   const double requested_price =
      side == "long"
      ? SymbolInfoDouble(_Symbol, SYMBOL_ASK)
      : SymbolInfoDouble(_Symbol, SYMBOL_BID);

   const ulong started = GetMicrosecondCount();
   bool ok = false;

   if(side == "long")
   {
      ok = g_trade.Buy(
         volume,
         _Symbol,
         0.0,
         stop_loss,
         take_profit > 0.0 ? take_profit : 0.0,
         broker_comment);
   }
   else if(side == "short")
   {
      ok = g_trade.Sell(
         volume,
         _Symbol,
         0.0,
         stop_loss,
         take_profit > 0.0 ? take_profit : 0.0,
         broker_comment);
   }
   else
   {
      EmitRejectedCommand(
         command_id,
         "submit",
         trade_intent_id,
         "INVALID_SIDE",
         "Submit side must be long or short.");
      return;
   }

   const double latency_ms =
      ((double)(GetMicrosecondCount() - started)) / 1000.0;

   const uint retcode = g_trade.ResultRetcode();
   const string outcome = OutcomeFromRetcode(retcode);
   const ulong order = g_trade.ResultOrder();
   const ulong deal = g_trade.ResultDeal();
   const double fill_price = g_trade.ResultPrice();
   const double filled_volume = g_trade.ResultVolume();

   ulong position_ticket = FindPositionByComment(
      broker_comment,
      magic_number);

   const bool has_fill = fill_price > 0.0;
   const bool has_slippage = has_fill && requested_price > 0.0;
   const double slippage_points =
      has_slippage && _Point > 0.0
      ? MathAbs(fill_price - requested_price) / _Point
      : 0.0;

   const bool safe_to_retry =
      (outcome == "requote")
      && order == 0
      && deal == 0
      && position_ticket == 0;

   string message = g_trade.ResultRetcodeDescription();
   if(!ok && StringLen(message) == 0)
      message = "CTrade submit returned false.";

   EmitExecutionResult(
      command_id,
      "submit",
      trade_intent_id,
      outcome,
      order > 0 ? IntegerToString((long)order) : "",
      deal > 0 ? IntegerToString((long)deal) : "",
      position_ticket > 0 ? IntegerToString((long)position_ticket) : "",
      requested_price,
      requested_price > 0.0,
      fill_price,
      has_fill,
      volume,
      filled_volume,
      slippage_points,
      has_slippage,
      latency_ms,
      IntegerToString((long)retcode),
      message,
      safe_to_retry);
}

void ExecuteModify(const string line)
{
   const string command_id = JsonString(line, "commandId");
   const string trade_intent_id = JsonString(line, "tradeIntentId");
   const string broker_comment = JsonString(line, "brokerComment");
   const long magic_number = JsonLong(line, "magicNumber", -1);
   const ulong position_ticket =
      (ulong)StringToInteger(JsonString(line, "brokerPositionId"));
   const double stop_loss = JsonDouble(line, "stopLossPrice", 0.0);
   const double take_profit = JsonDouble(line, "takeProfitPrice", 0.0);

   if(!SelectOwnedPosition(
         position_ticket,
         broker_comment,
         magic_number))
   {
      EmitRejectedCommand(
         command_id,
         "modify",
         trade_intent_id,
         "POSITION_OWNERSHIP_MISMATCH",
         "Position ticket/symbol/magic/comment ownership check failed.");
      return;
   }

   ConfigureTrade(magic_number, 0);

   const ulong started = GetMicrosecondCount();
   const bool ok = g_trade.PositionModify(
      position_ticket,
      stop_loss > 0.0 ? stop_loss : 0.0,
      take_profit > 0.0 ? take_profit : 0.0);
   const double latency_ms =
      ((double)(GetMicrosecondCount() - started)) / 1000.0;

   const uint retcode = g_trade.ResultRetcode();
   const string outcome = OutcomeFromRetcode(retcode);
   string message = g_trade.ResultRetcodeDescription();
   if(!ok && StringLen(message) == 0)
      message = "CTrade modify returned false.";

   EmitExecutionResult(
      command_id,
      "modify",
      trade_intent_id,
      outcome,
      g_trade.ResultOrder() > 0
         ? IntegerToString((long)g_trade.ResultOrder())
         : "",
      g_trade.ResultDeal() > 0
         ? IntegerToString((long)g_trade.ResultDeal())
         : "",
      IntegerToString((long)position_ticket),
      0.0,
      false,
      g_trade.ResultPrice(),
      g_trade.ResultPrice() > 0.0,
      0.0,
      g_trade.ResultVolume(),
      0.0,
      false,
      latency_ms,
      IntegerToString((long)retcode),
      message,
      false);
}

void ExecuteClose(const string line)
{
   const string command_id = JsonString(line, "commandId");
   const string trade_intent_id = JsonString(line, "tradeIntentId");
   const string broker_comment = JsonString(line, "brokerComment");
   const long magic_number = JsonLong(line, "magicNumber", -1);
   const int deviation_points = (int)JsonLong(line, "maxSlippagePoints", 0);
   const ulong position_ticket =
      (ulong)StringToInteger(JsonString(line, "brokerPositionId"));

   if(!SelectOwnedPosition(
         position_ticket,
         broker_comment,
         magic_number))
   {
      EmitRejectedCommand(
         command_id,
         "close",
         trade_intent_id,
         "POSITION_OWNERSHIP_MISMATCH",
         "Position ticket/symbol/magic/comment ownership check failed.");
      return;
   }

   const double requested_volume = PositionGetDouble(POSITION_VOLUME);
   const long position_type = PositionGetInteger(POSITION_TYPE);
   const double requested_price =
      position_type == POSITION_TYPE_BUY
      ? SymbolInfoDouble(_Symbol, SYMBOL_BID)
      : SymbolInfoDouble(_Symbol, SYMBOL_ASK);

   ConfigureTrade(magic_number, deviation_points);

   const ulong started = GetMicrosecondCount();
   const bool ok = g_trade.PositionClose(
      position_ticket,
      (ulong)MathMax(deviation_points, 0));
   const double latency_ms =
      ((double)(GetMicrosecondCount() - started)) / 1000.0;

   const uint retcode = g_trade.ResultRetcode();
   const string outcome = OutcomeFromRetcode(retcode);
   const double fill_price = g_trade.ResultPrice();
   const bool has_fill = fill_price > 0.0;
   const bool has_slippage = has_fill && requested_price > 0.0;
   const double slippage_points =
      has_slippage && _Point > 0.0
      ? MathAbs(fill_price - requested_price) / _Point
      : 0.0;

   string message = g_trade.ResultRetcodeDescription();
   if(!ok && StringLen(message) == 0)
      message = "CTrade close returned false.";

   EmitExecutionResult(
      command_id,
      "close",
      trade_intent_id,
      outcome,
      g_trade.ResultOrder() > 0
         ? IntegerToString((long)g_trade.ResultOrder())
         : "",
      g_trade.ResultDeal() > 0
         ? IntegerToString((long)g_trade.ResultDeal())
         : "",
      IntegerToString((long)position_ticket),
      requested_price,
      requested_price > 0.0,
      fill_price,
      has_fill,
      requested_volume,
      g_trade.ResultVolume(),
      slippage_points,
      has_slippage,
      latency_ms,
      IntegerToString((long)retcode),
      message,
      false);
}

string PositionJson(const ulong ticket)
{
   if(ticket == 0 || !PositionSelectByTicket(ticket))
      return "";

   const string broker_comment = PositionGetString(POSITION_COMMENT);
   const string trade_intent_id = TradeIntentForComment(broker_comment);
   const long position_type = PositionGetInteger(POSITION_TYPE);
   const long opened_at_ms = PositionGetInteger(POSITION_TIME_MSC);

   return StringFormat(
      "{\"tradeIntentId\":%s,\"brokerPositionId\":\"%s\","
      "\"brokerSymbol\":\"%s\",\"brokerComment\":%s,"
      "\"side\":\"%s\",\"volumeLots\":%s,\"entryPrice\":%s,"
      "\"stopLossPrice\":%s,\"takeProfitPrice\":%s,\"magicNumber\":%s,"
      "\"currentPrice\":%s,\"unrealizedPnlMoney\":%s,\"openedAtUnixMs\":%s}",
      JsonStringOrNull(trade_intent_id),
      IntegerToString((long)ticket),
      JsonEscape(PositionGetString(POSITION_SYMBOL)),
      JsonStringOrNull(broker_comment),
      position_type == POSITION_TYPE_BUY ? "long" : "short",
      DoubleToString(PositionGetDouble(POSITION_VOLUME), 8),
      DoubleToString(PositionGetDouble(POSITION_PRICE_OPEN), 10),
      JsonNumberOrNull(
         PositionGetDouble(POSITION_SL),
         PositionGetDouble(POSITION_SL) > 0.0,
         10),
      JsonNumberOrNull(
         PositionGetDouble(POSITION_TP),
         PositionGetDouble(POSITION_TP) > 0.0,
         10),
      IntegerToString((long)PositionGetInteger(POSITION_MAGIC)),
      DoubleToString(PositionGetDouble(POSITION_PRICE_CURRENT), 10),
      DoubleToString(PositionGetDouble(POSITION_PROFIT), 8),
      IntegerToString(opened_at_ms));
}

string OrderJson(const ulong ticket)
{
   if(ticket == 0)
      return "";

   const string broker_comment = OrderGetString(ORDER_COMMENT);
   const string trade_intent_id = TradeIntentForComment(broker_comment);

   return StringFormat(
      "{\"tradeIntentId\":%s,\"brokerOrderId\":\"%s\","
      "\"brokerSymbol\":\"%s\",\"brokerComment\":%s,"
      "\"magicNumber\":%s}",
      JsonStringOrNull(trade_intent_id),
      IntegerToString((long)ticket),
      JsonEscape(OrderGetString(ORDER_SYMBOL)),
      JsonStringOrNull(broker_comment),
      IntegerToString((long)OrderGetInteger(ORDER_MAGIC)));
}

bool MappingIsActive(
   const string broker_comment,
   const long magic_number)
{
   return FindPositionByComment(broker_comment, magic_number) > 0
      || FindOrderByComment(broker_comment, magic_number) > 0;
}

void ExecuteQueryState(const string line)
{
   const string command_id = JsonString(line, "commandId");
   const long magic_number = JsonLong(line, "magicNumber", -1);
   const ulong started = GetMicrosecondCount();

   string positions = "[";
   bool first_position = true;
   const int position_total = PositionsTotal();
   for(int i = 0; i < position_total; i++)
   {
      const ulong ticket = PositionGetTicket(i);
      if(ticket == 0 || !PositionSelectByTicket(ticket))
         continue;

      if(PositionGetString(POSITION_SYMBOL) != _Symbol)
         continue;

      if((long)PositionGetInteger(POSITION_MAGIC) != magic_number)
         continue;

      const string item = PositionJson(ticket);
      if(StringLen(item) == 0)
         continue;

      if(!first_position)
         positions += ",";
      positions += item;
      first_position = false;
   }
   positions += "]";

   string orders = "[";
   bool first_order = true;
   const int order_total = OrdersTotal();
   for(int i = 0; i < order_total; i++)
   {
      const ulong ticket = OrderGetTicket(i);
      if(ticket == 0)
         continue;

      if(OrderGetString(ORDER_SYMBOL) != _Symbol)
         continue;

      if((long)OrderGetInteger(ORDER_MAGIC) != magic_number)
         continue;

      const string item = OrderJson(ticket);
      if(StringLen(item) == 0)
         continue;

      if(!first_order)
         orders += ",";
      orders += item;
      first_order = false;
   }
   orders += "]";

   string closed = "[";
   bool first_closed = true;
   const int mapping_count = ArraySize(g_mapping_comments);
   for(int i = 0; i < mapping_count; i++)
   {
      if(g_mapping_magic_numbers[i] != magic_number)
         continue;

      if(MappingIsActive(
            g_mapping_comments[i],
            g_mapping_magic_numbers[i]))
         continue;

      if(!HasHistoryEntry(
            g_mapping_comments[i],
            g_mapping_magic_numbers[i]))
         continue;

      if(!first_closed)
         closed += ",";
      closed += JsonStringOrNull(g_mapping_trade_intents[i]);
      first_closed = false;
   }
   closed += "]";

   const double balance = AccountInfoDouble(ACCOUNT_BALANCE);
   const double equity = AccountInfoDouble(ACCOUNT_EQUITY);
   const double free_margin = AccountInfoDouble(ACCOUNT_MARGIN_FREE);

   const double point = SymbolInfoDouble(_Symbol, SYMBOL_POINT);
   const double tick_size = SymbolInfoDouble(
      _Symbol,
      SYMBOL_TRADE_TICK_SIZE);
   const double tick_value = SymbolInfoDouble(
      _Symbol,
      SYMBOL_TRADE_TICK_VALUE);
   const double min_volume = SymbolInfoDouble(
      _Symbol,
      SYMBOL_VOLUME_MIN);
   const double max_volume = SymbolInfoDouble(
      _Symbol,
      SYMBOL_VOLUME_MAX);
   const double volume_step = SymbolInfoDouble(
      _Symbol,
      SYMBOL_VOLUME_STEP);
   const long stop_level_points = SymbolInfoInteger(
      _Symbol,
      SYMBOL_TRADE_STOPS_LEVEL);
   const double min_stop_distance = stop_level_points * point;

   double estimated_margin_per_lot = 0.0;
   const double ask = SymbolInfoDouble(_Symbol, SYMBOL_ASK);
   if(ask > 0.0)
   {
      if(!OrderCalcMargin(
            ORDER_TYPE_BUY,
            _Symbol,
            1.0,
            ask,
            estimated_margin_per_lot))
      {
         estimated_margin_per_lot = 0.0;
      }
   }

   const string account = StringFormat(
      "{\"balance\":%s,\"equity\":%s,\"freeMargin\":%s}",
      DoubleToString(balance, 8),
      DoubleToString(equity, 8),
      DoubleToString(free_margin, 8));

   const string symbol_risk = StringFormat(
      "{\"point\":%s,\"tickSize\":%s,\"tickValue\":%s,"
      "\"minVolume\":%s,\"maxVolume\":%s,\"volumeStep\":%s,"
      "\"minStopDistance\":%s,\"estimatedMarginPerLotMoney\":%s}",
      DoubleToString(point, 10),
      DoubleToString(tick_size, 10),
      DoubleToString(tick_value, 10),
      DoubleToString(min_volume, 8),
      DoubleToString(max_volume, 8),
      DoubleToString(volume_step, 8),
      DoubleToString(min_stop_distance, 10),
      DoubleToString(estimated_margin_per_lot, 8));

   const double latency_ms =
      ((double)(GetMicrosecondCount() - started)) / 1000.0;

   const string json = StringFormat(
      "{\"protocolVersion\":\"%s\",\"type\":\"state\","
      "\"commandId\":\"%s\",\"bridgeSessionId\":\"%s\","
      "\"demoAccountVerified\":true,\"operation\":\"queryState\","
      "\"tradeIntentId\":null,\"outcome\":null,"
      "\"requestedVolumeLots\":0.0,\"filledVolumeLots\":0.0,"
      "\"latencyMs\":%s,\"safeToRetry\":false,"
      "\"positions\":%s,\"orders\":%s,"
      "\"closedTradeIntentIds\":%s,\"account\":%s,\"symbolRisk\":%s}",
      PROTOCOL_VERSION,
      JsonEscape(command_id),
      JsonEscape(g_session_id),
      DoubleToString(latency_ms, 3),
      positions,
      orders,
      closed,
      account,
      symbol_risk);

   if(WriteEvent(json))
      AddUnique(g_final_command_ids, command_id);
}

void RecoverDispatchedCommand(const string line)
{
   const string command_id = JsonString(line, "commandId");
   const string operation = JsonString(line, "operation");
   const string trade_intent_id = JsonString(line, "tradeIntentId");
   const string broker_comment = JsonString(line, "brokerComment");
   const long magic_number = JsonLong(line, "magicNumber", -1);

   if(operation != "submit")
   {
      EmitUnknownCommand(
         command_id,
         operation,
         trade_intent_id,
         "Bridge restarted after dispatch. Reconcile broker state before retrying this command.");
      return;
   }

   const ulong position_ticket =
      FindPositionByComment(broker_comment, magic_number);
   if(position_ticket > 0 && PositionSelectByTicket(position_ticket))
   {
      EmitExecutionResult(
         command_id,
         operation,
         trade_intent_id,
         "filled",
         "",
         "",
         IntegerToString((long)position_ticket),
         0.0,
         false,
         PositionGetDouble(POSITION_PRICE_OPEN),
         true,
         JsonDouble(line, "volumeLots", PositionGetDouble(POSITION_VOLUME)),
         PositionGetDouble(POSITION_VOLUME),
         0.0,
         false,
         0.0,
         "RECOVERED_POSITION",
         "Recovered owned broker position after bridge restart; command was not resent.",
         false);
      return;
   }

   const ulong order_ticket =
      FindOrderByComment(broker_comment, magic_number);
   if(order_ticket > 0)
   {
      EmitExecutionResult(
         command_id,
         operation,
         trade_intent_id,
         "accepted",
         IntegerToString((long)order_ticket),
         "",
         "",
         0.0,
         false,
         0.0,
         false,
         JsonDouble(line, "volumeLots", 0.0),
         0.0,
         0.0,
         false,
         0.0,
         "RECOVERED_ORDER",
         "Recovered owned broker order after bridge restart; command was not resent.",
         false);
      return;
   }

   if(HasHistoryEntry(broker_comment, magic_number))
   {
      EmitUnknownCommand(
         command_id,
         operation,
         trade_intent_id,
         "Entry history exists but no active position/order remains. QueryState will classify closed evidence.");
      return;
   }

   EmitUnknownCommand(
      command_id,
      operation,
      trade_intent_id,
      "No conclusive broker evidence after a pre-send dispatch marker. Command was not resent.");
}

void ProcessCommand(const string line)
{
   const string protocol = JsonString(line, "protocolVersion");
   const string command_id = JsonString(line, "commandId");
   const string session_id = JsonString(line, "bridgeSessionId");
   const string operation = JsonString(line, "operation");
   const string trade_intent_id = JsonString(line, "tradeIntentId");
   const string broker_symbol = JsonString(line, "brokerSymbol");
   const string broker_comment = JsonString(line, "brokerComment");
   const long magic_number = JsonLong(line, "magicNumber", -1);
   const bool demo_only = JsonBool(line, "demoOnly", false);

   if(operation == "submit")
   {
      RegisterMapping(
         broker_comment,
         trade_intent_id,
         magic_number);
   }

   if(StringLen(command_id) == 0)
      return;

   if(ContainsString(g_final_command_ids, command_id))
      return;

   if(ContainsString(g_dispatching_command_ids, command_id))
   {
      RecoverDispatchedCommand(line);
      return;
   }

   if(protocol != PROTOCOL_VERSION)
   {
      EmitRejectedCommand(
         command_id,
         operation,
         trade_intent_id,
         "PROTOCOL_MISMATCH",
         "Command protocol version does not match bridge.");
      return;
   }

   if(!demo_only)
   {
      EmitRejectedCommand(
         command_id,
         operation,
         trade_intent_id,
         "DEMO_ONLY_REQUIRED",
         "Bridge refuses commands without demoOnly=true.");
      return;
   }

   if(session_id != g_session_id)
   {
      EmitRejectedCommand(
         command_id,
         operation,
         trade_intent_id,
         "BRIDGE_SESSION_MISMATCH",
         "Command belongs to an old or different bridge session and was not executed.");
      return;
   }

   if(broker_symbol != _Symbol)
   {
      EmitRejectedCommand(
         command_id,
         operation,
         trade_intent_id,
         "SYMBOL_MISMATCH",
         "Command brokerSymbol does not match chart symbol.");
      return;
   }

   if(magic_number != (long)InpMagicNumber)
   {
      EmitRejectedCommand(
         command_id,
         operation,
         trade_intent_id,
         "MAGIC_MISMATCH",
         "Command magic number does not match bridge configuration.");
      return;
   }

   if((ENUM_ACCOUNT_TRADE_MODE)AccountInfoInteger(ACCOUNT_TRADE_MODE)
      != ACCOUNT_TRADE_MODE_DEMO)
   {
      EmitRejectedCommand(
         command_id,
         operation,
         trade_intent_id,
         "NON_DEMO_ACCOUNT",
         "Bridge detected a non-demo account and refused execution.");
      return;
   }

   if(operation == "queryState")
   {
      ExecuteQueryState(line);
      return;
   }

   EmitDispatching(
      command_id,
      operation,
      trade_intent_id);

   if(operation == "submit")
   {
      ExecuteSubmit(line);
      return;
   }

   if(operation == "modify")
   {
      ExecuteModify(line);
      return;
   }

   if(operation == "close")
   {
      ExecuteClose(line);
      return;
   }

   EmitRejectedCommand(
      command_id,
      operation,
      trade_intent_id,
      "UNSUPPORTED_OPERATION",
      "Unsupported demo execution operation.");
}

void ScanExistingEvents()
{
   int handle = FileOpen(
      InpEventFile,
      FILE_READ | FILE_TXT | FILE_ANSI | FILE_COMMON
      | FILE_SHARE_READ | FILE_SHARE_WRITE);

   if(handle == INVALID_HANDLE)
      return;

   while(!FileIsEnding(handle))
   {
      const string line = FileReadString(handle);
      if(StringLen(line) == 0)
         continue;

      const string command_id = JsonString(line, "commandId");
      const string type = JsonString(line, "type");
      if(StringLen(command_id) == 0)
         continue;

      if(type == "dispatching")
         AddUnique(g_dispatching_command_ids, command_id);
      else if(type == "executionResult" || type == "state")
         AddUnique(g_final_command_ids, command_id);
   }

   FileClose(handle);
}

void ProcessCommandFile()
{
   int handle = FileOpen(
      InpCommandFile,
      FILE_READ | FILE_TXT | FILE_ANSI | FILE_COMMON
      | FILE_SHARE_READ | FILE_SHARE_WRITE);

   if(handle == INVALID_HANDLE)
      return;

   int line_number = 0;

   while(!FileIsEnding(handle))
   {
      const string line = FileReadString(handle);

      if(line_number >= g_command_lines_seen
         && StringLen(line) > 0)
      {
         ProcessCommand(line);
      }

      line_number++;
   }

   g_command_lines_seen = line_number;
   FileClose(handle);
}

int OnInit()
{
   if((ENUM_ACCOUNT_TRADE_MODE)AccountInfoInteger(ACCOUNT_TRADE_MODE)
      != ACCOUNT_TRADE_MODE_DEMO)
   {
      Print(
         "XauScalp demo execution bridge REFUSES non-demo accounts. "
         "Attach this EA only to an MT5 demo account.");
      return INIT_FAILED;
   }

   if(InpPollIntervalMs <= 0 || InpHeartbeatIntervalMs <= 0)
   {
      Print("XauScalp demo bridge timing inputs must be positive.");
      return INIT_PARAMETERS_INCORRECT;
   }

   FolderCreate("XauScalp", FILE_COMMON);

   g_event_file = FileOpen(
      InpEventFile,
      FILE_READ | FILE_WRITE | FILE_TXT | FILE_ANSI | FILE_COMMON
      | FILE_SHARE_READ | FILE_SHARE_WRITE);

   if(g_event_file == INVALID_HANDLE)
   {
      PrintFormat(
         "Cannot open XauScalp demo execution event file '%s'. MQL5 error=%d",
         InpEventFile,
         GetLastError());
      return INIT_FAILED;
   }

   g_session_id = StringFormat(
      "%s-%s-%s",
      IntegerToString((long)TimeGMT()),
      IntegerToString((long)ChartID()),
      IntegerToString((long)GetTickCount64()));

   ScanExistingEvents();

   FileSeek(g_event_file, 0, SEEK_END);
   EmitBridgeReady();
   EmitHeartbeat();

   if(!EventSetMillisecondTimer(InpPollIntervalMs))
   {
      PrintFormat(
         "Cannot start XauScalp demo bridge timer. MQL5 error=%d",
         GetLastError());
      FileClose(g_event_file);
      g_event_file = INVALID_HANDLE;
      return INIT_FAILED;
   }

   ProcessCommandFile();

   return INIT_SUCCEEDED;
}

void OnDeinit(const int reason)
{
   EventKillTimer();

   if(g_event_file != INVALID_HANDLE)
   {
      FileFlush(g_event_file);
      FileClose(g_event_file);
      g_event_file = INVALID_HANDLE;
   }
}

void OnTimer()
{
   if((ENUM_ACCOUNT_TRADE_MODE)AccountInfoInteger(ACCOUNT_TRADE_MODE)
      != ACCOUNT_TRADE_MODE_DEMO)
   {
      Print(
         "XauScalp demo bridge detected non-demo mode after init. "
         "No command will be executed.");
      return;
   }

   const ulong now_ms = GetTickCount64();
   if((long)(now_ms - g_last_heartbeat_ms)
      >= InpHeartbeatIntervalMs)
   {
      EmitHeartbeat();
   }

   ProcessCommandFile();
}
