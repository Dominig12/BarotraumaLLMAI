namespace LlmAgents.Agents;

public enum TreatmentBlockReason { MissingStock, RecoveryPredicted, NotBeneficialNow }

/// <summary>A native treatment recommendation can disappear while earlier doses are still taking effect.
/// That is a reason to reassess the patient, not evidence that the submarine has run out of medicine.</summary>
public static class TreatmentRules
{
    public static TreatmentBlockReason NoChoice(int matchingStock, bool beneficialWithoutPrediction) =>
        matchingStock <= 0 ? TreatmentBlockReason.MissingStock
        : beneficialWithoutPrediction ? TreatmentBlockReason.RecoveryPredicted
        : TreatmentBlockReason.NotBeneficialNow;
}
