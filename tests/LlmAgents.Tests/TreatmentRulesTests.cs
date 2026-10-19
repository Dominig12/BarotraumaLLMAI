using LlmAgents.Agents;
using Xunit;

namespace LlmAgents.Tests;

public class TreatmentRulesTests
{
    [Fact]
    public void PredictedRecoveryDoesNotTurnStockedMorphineIntoMissingMedicine() =>
        Assert.Equal(TreatmentBlockReason.RecoveryPredicted, TreatmentRules.NoChoice(10, beneficialWithoutPrediction: true));

    [Fact]
    public void StockedButUnhelpfulMedicineIsNotReportedAsOutOfStock() =>
        Assert.Equal(TreatmentBlockReason.NotBeneficialNow, TreatmentRules.NoChoice(29, beneficialWithoutPrediction: false));

    [Fact]
    public void NoMatchingAccessibleStockIsARealMissingMedicineProblem() =>
        Assert.Equal(TreatmentBlockReason.MissingStock, TreatmentRules.NoChoice(0, beneficialWithoutPrediction: false));
}
