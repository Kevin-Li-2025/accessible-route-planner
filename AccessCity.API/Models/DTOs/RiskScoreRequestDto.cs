namespace AccessCity.API.Models.DTOs
{
    public class RiskScoreRequestDto
    {
        public double Lat { get; set; }
        public double Lng { get; set; }
        public double Radius { get; set; } = 500;
    }
}
