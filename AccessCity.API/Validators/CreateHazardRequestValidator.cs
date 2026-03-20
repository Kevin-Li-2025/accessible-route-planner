using FluentValidation;
using AccessCity.API.Models.DTOs;

namespace AccessCity.API.Validators
{
    public class CreateHazardRequestValidator : AbstractValidator<CreateHazardRequest>
    {
        public CreateHazardRequestValidator()
        {
            RuleFor(x => x.Location)
                .NotNull().WithMessage("Location is required.");

            RuleFor(x => x.Location.X)
                .InclusiveBetween(-180, 180)
                .WithMessage("Longitude must be between -180 and 180.");

            RuleFor(x => x.Location.Y)
                .InclusiveBetween(-90, 90)
                .WithMessage("Latitude must be between -90 and 90.");

            RuleFor(x => x.Type)
                .NotEmpty().WithMessage("Type is required.")
                .MaximumLength(50).WithMessage("Type cannot exceed 50 characters.");

            RuleFor(x => x.Description)
                .NotEmpty().WithMessage("Description is required.")
                .MaximumLength(500).WithMessage("Description cannot exceed 500 characters.");
            
            RuleFor(x => x.PhotoUrl)
                .MaximumLength(2048).WithMessage("Photo URL cannot exceed 2048 characters.");
        }
    }
}
