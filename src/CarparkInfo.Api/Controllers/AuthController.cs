using System.ComponentModel.DataAnnotations;
using CarparkInfo.Application.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CarparkInfo.Api.Controllers;

/// <summary>Registration, sign-in and token rotation.</summary>
[ApiController]
[Route("api/v1/auth")]
[Produces("application/json")]
[AllowAnonymous]
public sealed class AuthController : ControllerBase
{
    private readonly AuthenticationService _auth;

    /// <summary>Creates the controller.</summary>
    /// <param name="auth">Authentication service.</param>
    public AuthController(AuthenticationService auth) => _auth = auth;

    /// <summary>
    /// Registers an account.
    /// </summary>
    /// <param name="request">Email, password and display name.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A generic acknowledgement.</returns>
    /// <remarks>
    /// Always returns the same message and takes the same time whether or not the address is
    /// already registered. Distinguishing the two would turn this endpoint into a way to discover
    /// which email addresses have accounts.
    /// </remarks>
    /// <response code="200">Acknowledged.</response>
    /// <response code="400">The request was malformed.</response>
    [HttpPost("register")]
    [ProducesResponseType(typeof(MessageResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<MessageResponse>> Register(
        [FromBody] RegisterRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var message = await _auth
            .RegisterAsync(request.Email, request.Password, request.DisplayName ?? request.Email,
                cancellationToken)
            .ConfigureAwait(false);

        return Ok(new MessageResponse(message));
    }

    /// <summary>
    /// Signs in and issues tokens.
    /// </summary>
    /// <param name="request">Email and password.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>An access token and a refresh token.</returns>
    /// <remarks>
    /// Hold the access token in memory and the refresh token in secure storage — Keychain,
    /// Keystore, or an httpOnly cookie for web. On a `401`, exchange the refresh token once and
    /// retry; a second `401` means re-authenticate.
    ///
    /// Five failed attempts lock the account for fifteen minutes.
    /// </remarks>
    /// <response code="200">Tokens issued.</response>
    /// <response code="401">Sign-in failed. The message is deliberately generic.</response>
    [HttpPost("login")]
    [ProducesResponseType(typeof(AuthTokenResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<AuthTokenResponse>> Login(
        [FromBody] LoginRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await _auth
            .SignInAsync(request.Email, request.Password, ClientIp, cancellationToken)
            .ConfigureAwait(false);

        return result.Succeeded
            ? Ok(AuthTokenResponse.From(result.Tokens!))
            : Problem(title: "Authentication failed", detail: result.FailureReason,
                statusCode: StatusCodes.Status401Unauthorized);
    }

    /// <summary>
    /// Exchanges a refresh token for a new pair.
    /// </summary>
    /// <param name="request">The refresh token.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A new access token and a new refresh token.</returns>
    /// <remarks>
    /// Refresh tokens are **single-use**. Each exchange returns a new one and revokes the old.
    ///
    /// Presenting a token that has already been used means a copy is in circulation, so **every
    /// session for that account is revoked** and the user must sign in again. Rotation without
    /// that detection simply lets a stolen token work quietly until it expires.
    /// </remarks>
    /// <response code="200">New tokens issued.</response>
    /// <response code="401">The token was invalid, expired, or already used.</response>
    [HttpPost("refresh")]
    [ProducesResponseType(typeof(AuthTokenResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<AuthTokenResponse>> Refresh(
        [FromBody] RefreshRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await _auth
            .RefreshAsync(request.RefreshToken, ClientIp, cancellationToken)
            .ConfigureAwait(false);

        return result.Succeeded
            ? Ok(AuthTokenResponse.From(result.Tokens!))
            : Problem(title: "Token refresh failed", detail: result.FailureReason,
                statusCode: StatusCodes.Status401Unauthorized);
    }

    /// <summary>Revokes a refresh token.</summary>
    /// <param name="request">The refresh token to revoke.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>No content.</returns>
    /// <remarks>
    /// Returns `204` whether or not the token existed: reporting "no such token" would let a
    /// caller probe which tokens are valid.
    /// </remarks>
    /// <response code="204">The token is no longer usable.</response>
    [HttpPost("logout")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout(
        [FromBody] RefreshRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        await _auth.SignOutAsync(request.RefreshToken, cancellationToken).ConfigureAwait(false);

        return NoContent();
    }

    private string? ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString();
}

/// <summary>Registration request.</summary>
/// <param name="Email">The email address.</param>
/// <param name="Password">The password. At least 12 characters.</param>
/// <param name="DisplayName">A human-readable name. Defaults to the email address.</param>
public sealed record RegisterRequest(
    [Required, EmailAddress, MaxLength(256)] string Email,
    [Required, MinLength(12), MaxLength(128)] string Password,
    [MaxLength(128)] string? DisplayName);

/// <summary>Sign-in request.</summary>
/// <param name="Email">The email address.</param>
/// <param name="Password">The password.</param>
public sealed record LoginRequest(
    [Required, EmailAddress] string Email,
    [Required] string Password);

/// <summary>Refresh or logout request.</summary>
/// <param name="RefreshToken">The refresh token issued by a previous call.</param>
public sealed record RefreshRequest([Required] string RefreshToken);

/// <summary>Issued tokens.</summary>
/// <param name="AccessToken">Bearer token for the Authorization header. Hold in memory.</param>
/// <param name="RefreshToken">Single-use token for renewal. Store securely.</param>
/// <param name="ExpiresAt">When the access token expires.</param>
/// <param name="ExpiresInSeconds">Access-token lifetime in seconds.</param>
/// <param name="TokenType">Always <c>Bearer</c>.</param>
public sealed record AuthTokenResponse(
    string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt, int ExpiresInSeconds,
    string TokenType)
{
    /// <summary>Maps from the application result.</summary>
    /// <param name="tokens">The issued tokens.</param>
    /// <returns>The response.</returns>
    public static AuthTokenResponse From(AuthTokens tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        return new AuthTokenResponse(tokens.AccessToken, tokens.RefreshToken, tokens.ExpiresAt,
            tokens.ExpiresInSeconds, "Bearer");
    }
}

/// <summary>A simple message response.</summary>
/// <param name="Message">The message.</param>
public sealed record MessageResponse(string Message);
