using FluentValidation;
using AccessCity.API.Models;

namespace AccessCity.API.Validators
{
    public class RouteRequestValidator : AbstractValidator<RouteRequest>
    {
        public RouteRequestValidator()
        {
            RuleFor(x => x.Start)
                .NotNull().WithMessage("Start coordinate is required.");

            RuleFor(x => x.End)
                .NotNull().WithMessage("End coordinate is required.");

            When(x => x.Start != null, () =>
            {
                RuleFor(x => x.Start.X)
                    .InclusiveBetween(-180, 180)
                    .WithMessage("Start Longitude must be between -180 and 180.");

                RuleFor(x => x.Start.Y)
                    .InclusiveBetween(-90, 90)
                    .WithMessage("Start Latitude must be between -90 and 90.");
            });

            When(x => x.End != null, () =>
            {
                RuleFor(x => x.End.X)
                    .InclusiveBetween(-180, 180)
                    .WithMessage("End Longitude must be between -180 and 180.");

                RuleFor(x => x.End.Y)
                    .InclusiveBetween(-90, 90)
                    .WithMessage("End Latitude must be between -90 and 90.");
            });

            RuleFor(x => x.SafetyWeight)
                .InclusiveBetween(0, 1)
                .WithMessage("SafetyWeight must be between 0 and 1.");

            RuleFor(x => x.Profile)
                .NotEmpty().WithMessage("Profile is required.")
                .Must(x => new[] { "standard", "manual-wheelchair", "power-wheelchair", "stroller" }.Contains(x))
                .WithMessage("Profile must be one of: standard, manual-wheelchair, power-wheelchair, stroller.");
        }
    }
}
