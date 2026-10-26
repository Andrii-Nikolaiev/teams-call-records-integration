using Microsoft.Teams.Apps;

namespace TeamsCallRecords.Host.Bot;

/// <summary>
/// Connects the Teams SDK to <see cref="BotCommands"/>. Authentication of /api/messages is done by the SDK itself
/// (TRP 3.24): a request without a valid token is answered with 401 before this handler runs.
/// </summary>
public static class TcriBot
{
    public static void RegisterHandlers(TeamsBotApplication teams, BotCommands commands, TimeProvider clock, TimeZoneInfo timeZone)
    {
        teams.OnMessage(async (context, ct) =>
        {
            var answer = await commands.ExecuteAsync(context.Activity.Text ?? string.Empty, clock.GetUtcNow(), timeZone, ct);
            await context.SendAsync(answer, ct);
        });
    }
}
