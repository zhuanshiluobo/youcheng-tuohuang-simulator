using YC.Domain.Commands;

namespace YC.Domain.CityStyles
{
    public sealed class DeclareCityStyleResult
    {
        private DeclareCityStyleResult(
            bool succeeded,
            ValidationResult validation,
            CityStyleDefinition cityStyle,
            CityStyleMatchResult match)
        {
            Succeeded = succeeded;
            Validation = validation;
            CityStyle = cityStyle;
            Match = match;
        }

        public bool Succeeded { get; private set; }
        public ValidationResult Validation { get; private set; }
        public CityStyleDefinition CityStyle { get; private set; }
        public CityStyleMatchResult Match { get; private set; }

        public static DeclareCityStyleResult Success(CityStyleDefinition cityStyle, CityStyleMatchResult match)
        {
            return new DeclareCityStyleResult(true, ValidationResult.Success, cityStyle, match);
        }

        public static DeclareCityStyleResult Failure(ValidationResult validation)
        {
            return new DeclareCityStyleResult(false, validation, null, null);
        }
    }
}
