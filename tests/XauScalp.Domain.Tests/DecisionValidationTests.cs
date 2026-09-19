namespace XauScalp.Domain.Tests;

public sealed class DecisionValidationTests
{
    [Fact]
    public void Probability_RejectsNaN()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ContractTestFactory.CreateDecision(double.NaN));
    }

    [Fact]
    public void Probability_RejectsPositiveInfinity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ContractTestFactory.CreateDecision(double.PositiveInfinity));
    }

    [Fact]
    public void Probability_RejectsNegativeInfinity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ContractTestFactory.CreateDecision(double.NegativeInfinity));
    }

    [Fact]
    public void Probability_RejectsBelowZero()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ContractTestFactory.CreateDecision(-0.0001));
    }

    [Fact]
    public void Probability_RejectsAboveOne()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ContractTestFactory.CreateDecision(1.0001));
    }

    [Fact]
    public void OptionalProbability_UsesSameValidation()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ContractTestFactory.CreateDecision(pHold: 2));
    }
}
