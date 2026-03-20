using FluentValidation;
using AccessCity.API.Models.DTOs;

namespace AccessCity.API.Validators
{
    public class RiskScoreRequestValidator : AbstractValidator<RiskScoreRequestDto>
    {
        public RiskScoreRequestValidator()
        {
            RuleFor(x => x.Lat)
                .InclusiveBetween(-90, 90).WithMessage("Latitude must be between -90 and 90.");

            RuleFor(x => x.Lng)
                .InclusiveBetween(-180, 180).WithMessage("Longitude must be between -180 and 180.");

            RuleFor(x => x.Radius)
                .InclusiveBetween(1, 5000).WithMessage("Radius must be between 1 and 5000 metres.");
        }
    }
}
