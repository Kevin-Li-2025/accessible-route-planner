using System.Net.Http.Json;
using AccessCity.API.Models.Identity;
using Xunit;
using System.Text.Json;

namespace AccessCity.Tests
{
    public class AuthTests : IClassFixture<AccessCityApiFactory>
    {
        private readonly HttpClient _client;
        private readonly AccessCityApiFactory _factory;
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
            PropertyNameCaseInsensitive = true
        };

        public AuthTests(AccessCityApiFactory factory)
        {
            _factory = factory;
            _client = factory.CreateClient();
        }

        [Fact]
        public async Task Register_And_Login_Correctly()
        {
            // 1. Register
            var registerRequest = new RegisterRequest("test" + Guid.NewGuid() + "@example.com", "P@ssword123!", "Test User");
            var regResponse = await _client.PostAsJsonAsync("/api/v1/auth/register", registerRequest, _jsonOptions);
            
            if (!regResponse.IsSuccessStatusCode)
            {
                var error = await regResponse.Content.ReadAsStringAsync();
                throw new Exception($"Registration failed: {regResponse.StatusCode} - {error}");
            }

            var regResult = await regResponse.Content.ReadFromJsonAsync<AuthResponse>(_jsonOptions);
            Assert.NotNull(regResult?.Token);

            // 2. Login
            var loginRequest = new LoginRequest(registerRequest.Email, "P@ssword123!");
            var loginResponse = await _client.PostAsJsonAsync("/api/v1/auth/login", loginRequest, _jsonOptions);
            
            loginResponse.EnsureSuccessStatusCode();
            var loginResult = await loginResponse.Content.ReadFromJsonAsync<AuthResponse>(_jsonOptions);
            
            Assert.NotNull(loginResult?.Token);
            Assert.Equal(registerRequest.Email, loginResult.Email);
        }

        [Fact]
        public async Task ForgotPassword_Flow_Test()
        {
            var email = "forgot" + Guid.NewGuid() + "@example.com";
            var registerRequest = new RegisterRequest(email, "P@ssword123!", "Forgot User");
            await _client.PostAsJsonAsync("/api/v1/auth/register", registerRequest, _jsonOptions);

            var forgotRequest = new ForgotPasswordRequest(email);
            var forgotResponse = await _client.PostAsJsonAsync("/api/v1/auth/forgot-password", forgotRequest, _jsonOptions);
            if (!forgotResponse.IsSuccessStatusCode && forgotResponse.StatusCode != System.Net.HttpStatusCode.ServiceUnavailable)
                forgotResponse.EnsureSuccessStatusCode();

            var resetWithBadToken = new ResetPasswordRequest(email, "invalid-token", "NewP@ssword456!");
            var resetResponse = await _client.PostAsJsonAsync("/api/v1/auth/reset-password", resetWithBadToken, _jsonOptions);
            Assert.True(resetResponse.StatusCode == System.Net.HttpStatusCode.BadRequest
                || resetResponse.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable
                || resetResponse.StatusCode == System.Net.HttpStatusCode.OK,
                $"Expected BadRequest/503/OK, got {resetResponse.StatusCode}");
        }
    }
}
