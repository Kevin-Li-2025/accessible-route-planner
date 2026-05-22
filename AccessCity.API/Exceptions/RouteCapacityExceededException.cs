namespace AccessCity.API.Exceptions;

public sealed class RouteCapacityExceededException : Exception
{
    public RouteCapacityExceededException()
        : base("Route computation capacity is saturated.")
    {
    }
}
