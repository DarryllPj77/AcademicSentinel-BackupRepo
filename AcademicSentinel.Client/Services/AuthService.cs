using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using AcademicSentinel.Client.Models;
using AcademicSentinel.Client.Constants; // Added this using statement

namespace AcademicSentinel.Client.Services
{
    public class AuthService
    {
        private readonly HttpClient _httpClient;
        public string? LastErrorMessage { get; set; }

        public AuthService()
        {
            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(30);
        }

        // Calls POST /api/auth/register
        public async Task<bool> RegisterAsync(UserRegisterDto registerData)
        {
            LastErrorMessage = null;
            try
            {
                // Hits the /api/auth/register endpoint we just created
                var response = await _httpClient.PostAsJsonAsync(ApiEndpoints.AuthRegister, registerData);
                if (response.IsSuccessStatusCode)
                    return true;

                // Surface the real server reason so the UI can show
                // "domain not allowed" vs "email already registered" vs
                // "wait N seconds" instead of one generic message.
                try
                {
                    var body = await response.Content.ReadAsStringAsync();
                    LastErrorMessage = string.IsNullOrWhiteSpace(body)
                        ? $"Server returned {(int)response.StatusCode} {response.StatusCode}."
                        : body;
                }
                catch
                {
                    LastErrorMessage = $"Server returned {(int)response.StatusCode} {response.StatusCode}.";
                }
                return false;
            }
            catch (HttpRequestException hre)
            {
                // Cannot reach the API at all — usually means the
                // BaseUrl in ApiEndpoints.cs points somewhere the
                // installed client can't actually hit (e.g. localhost
                // on a deployed build).
                LastErrorMessage = $"Cannot reach server: {hre.Message}";
                return false;
            }
            catch (TaskCanceledException)
            {
                LastErrorMessage = "The server did not respond in time. Check your internet connection.";
                return false;
            }
            catch (Exception ex)
            {
                LastErrorMessage = $"Unexpected error: {ex.Message}";
                return false;
            }
        }

        // Calls POST /api/auth/login
        public async Task<bool> LoginAsync(string email, string password)
        {
            LastErrorMessage = null;
            try
            {
                var loginData = new UserLoginDto { Email = email, Password = password };

                // Now using ApiEndpoints.AuthLogin!
                var response = await _httpClient.PostAsJsonAsync(ApiEndpoints.AuthLogin, loginData);

                if (response.IsSuccessStatusCode)
                {
                    var result = await response.Content.ReadFromJsonAsync<UserResponseDto>();
                    if (result != null)
                    {
                        SessionManager.CurrentUser = result;
                        SessionManager.JwtToken = result.Token;
                        return true;
                    }
                    return false;
                }

                // Surface a marker for the server's EMAIL_NOT_VERIFIED
                // 403 so the LoginWindow can branch and route the user
                // to the verify-email screen instead of treating it as
                // a bad-credentials failure. The server replies with
                // either a JSON object that carries a `code` field or
                // a plain string for legacy paths; treat any 403 as
                // the verification gate.
                if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    LastErrorMessage = "EMAIL_NOT_VERIFIED";
                    return false;
                }

                // Single-device session lock — server returns 409 when
                // the account is already logged in elsewhere. Surface a
                // distinct marker so the LoginWindow can show the
                // actionable "already logged in on another device"
                // message instead of the generic invalid-credentials
                // toast.
                if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
                {
                    LastErrorMessage = "ALREADY_LOGGED_IN";
                    return false;
                }

                LastErrorMessage = "INVALID_CREDENTIALS";
                return false;
            }
            catch (Exception ex)
            {
                LastErrorMessage = $"NETWORK_ERROR:{ex.Message}";
                return false;
            }
        }

        public async Task<bool> RequestPasswordResetCodeAsync(string email)
        {
            try
            {
                var request = new ForgotPasswordRequestDto { Email = email };
                var response = await _httpClient.PostAsJsonAsync(ApiEndpoints.AuthForgotPassword, request);

                if (!response.IsSuccessStatusCode)
                {
                    try
                    {
                        var errorContent = await response.Content.ReadAsStringAsync();
                        LastErrorMessage = $"Server error ({response.StatusCode}): {errorContent}";
                    }
                    catch
                    {
                        LastErrorMessage = $"Server returned error: {response.StatusCode}";
                    }
                    return false;
                }

                LastErrorMessage = null;
                return true;
            }
            catch (HttpRequestException hre)
            {
                LastErrorMessage = $"Network error: {hre.Message}. Check if the server is running.";
                return false;
            }
            catch (Exception ex)
            {
                LastErrorMessage = $"Unexpected error: {ex.Message}";
                return false;
            }
        }

        public async Task<string?> VerifyPasswordResetCodeAsync(string email, string code)
        {
            try
            {
                var request = new VerifyResetCodeRequestDto
                {
                    Email = email,
                    Code = code
                };

                var response = await _httpClient.PostAsJsonAsync(ApiEndpoints.AuthVerifyResetCode, request);
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                var result = await response.Content.ReadFromJsonAsync<VerifyResetCodeResponseDto>();
                if (result == null || !result.Success || string.IsNullOrWhiteSpace(result.ResetToken))
                {
                    return null;
                }

                return result.ResetToken;
            }
            catch
            {
                return null;
            }
        }

        public async Task<bool> ResetPasswordAsync(string email, string newPassword, string resetToken)
        {
            try
            {
                var request = new ResetPasswordRequestDto
                {
                    Email = email,
                    NewPassword = newPassword,
                    ResetToken = resetToken
                };

                var response = await _httpClient.PostAsJsonAsync(ApiEndpoints.AuthResetPassword, request);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }
    }
}