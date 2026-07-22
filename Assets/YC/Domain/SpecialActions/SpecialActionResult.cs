using System;
using System.Collections.Generic;
using YC.Domain.Commands;
using YC.Domain.State;

namespace YC.Domain.SpecialActions
{
    [Serializable]
    public sealed class SpecialActionPaymentOption
    {
        public string OptionId = string.Empty;
        public int Originium;
        public int OriginiumShard;
        public int Iron;
        public int GoldVoucher;

        public ResourceSet ToResourceSet()
        {
            return new ResourceSet
            {
                Originium = Originium,
                OriginiumShard = OriginiumShard,
                Iron = Iron,
                GoldVoucher = GoldVoucher
            };
        }
    }

    [Serializable]
    public sealed class SpecialActionOption
    {
        public string SpecialActionId = string.Empty;
        public string CityStyleId = string.Empty;
        public string Name = string.Empty;
        public string Description = string.Empty;
        public string DeclarationMarkerId = string.Empty;
        public string MarkerArea = string.Empty;
        public int RemainingUses;
        public bool UsedThisRound;
        public bool CanUse;
        public string DisabledReason = string.Empty;
        public string Warning = string.Empty;
        public int RequiredTargetCount;
        public ResourceSet FixedCost = new ResourceSet();
        public List<SpecialActionPaymentOption> PaymentOptions = new List<SpecialActionPaymentOption>();
        public List<string> LegalInfluenceSlotIds = new List<string>();
        public List<string> ReplaceableInfluenceSlotIds = new List<string>();
        public List<string> LegalMoveTargetIds = new List<string>();
    }

    [Serializable]
    public sealed class SpecialActionOptionQueryResult
    {
        public List<SpecialActionOption> Options = new List<SpecialActionOption>();

        public bool HasUsableOption
        {
            get
            {
                for (var i = 0; i < Options.Count; i++)
                {
                    if (Options[i] != null && Options[i].CanUse)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        public SpecialActionOption Find(string specialActionId, string declarationMarkerId)
        {
            for (var i = 0; i < Options.Count; i++)
            {
                var option = Options[i];
                if (option != null &&
                    option.SpecialActionId == specialActionId &&
                    option.DeclarationMarkerId == declarationMarkerId)
                {
                    return option;
                }
            }

            return null;
        }
    }

    public sealed class SpecialActionOperationResult
    {
        public bool Succeeded { get; private set; }
        public ValidationResult Validation { get; private set; }
        public bool Completed { get; private set; }
        public SpecialActionDefinition Definition { get; private set; }
        public string Summary { get; private set; }

        private SpecialActionOperationResult(
            bool succeeded,
            ValidationResult validation,
            bool completed,
            SpecialActionDefinition definition,
            string summary)
        {
            Succeeded = succeeded;
            Validation = validation;
            Completed = completed;
            Definition = definition;
            Summary = summary ?? string.Empty;
        }

        public static SpecialActionOperationResult Failure(ValidationResult validation)
        {
            return new SpecialActionOperationResult(false, validation, false, null, validation == null ? string.Empty : validation.Reason);
        }

        public static SpecialActionOperationResult Success(
            SpecialActionDefinition definition,
            bool completed,
            string summary)
        {
            return new SpecialActionOperationResult(true, ValidationResult.Success, completed, definition, summary);
        }
    }
}
