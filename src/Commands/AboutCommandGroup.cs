﻿using System.ComponentModel;
using System.Text;
using JetBrains.Annotations;
using Octobot.Data;
using Octobot.Extensions;
using Octobot.Services;
using Remora.Commands.Attributes;
using Remora.Commands.Groups;
using Remora.Discord.API.Abstractions.Objects;
using Remora.Discord.API.Abstractions.Rest;
using Remora.Discord.API.Objects;
using Remora.Discord.Commands.Attributes;
using Remora.Discord.Commands.Conditions;
using Remora.Discord.Commands.Contexts;
using Remora.Discord.Commands.Feedback.Messages;
using Remora.Discord.Commands.Feedback.Services;
using Remora.Discord.Extensions.Embeds;
using Remora.Discord.Extensions.Formatting;
using Remora.Rest.Core;
using Remora.Results;

namespace Octobot.Commands;

/// <summary>
///     Handles the command to show information about this bot: /about.
/// </summary>
[UsedImplicitly]
public class AboutCommandGroup : CommandGroup
{
    private static readonly (string Username, Snowflake Id)[] Developers =
    [
        ("Octol1ttle", new Snowflake(504343489664909322)),
        ("mctaylors", new Snowflake(326642240229474304)),
        ("neroduckale", new Snowflake(474943797063843851))
    ];

    private readonly ICommandContext _context;
    private readonly IFeedbackService _feedback;
    private readonly GuildDataService _guildData;
    private readonly IDiscordRestUserAPI _userApi;
    private readonly IDiscordRestGuildAPI _guildApi;

    public AboutCommandGroup(
        ICommandContext context, GuildDataService guildData,
        IFeedbackService feedback, IDiscordRestUserAPI userApi,
        IDiscordRestGuildAPI guildApi)
    {
        _context = context;
        _guildData = guildData;
        _feedback = feedback;
        _userApi = userApi;
        _guildApi = guildApi;
    }

    /// <summary>
    ///     A slash command that shows information about this bot.
    /// </summary>
    /// <returns>
    ///     A feedback sending result which may or may not have succeeded.
    /// </returns>
    [Command("about")]
    [DiscordDefaultDMPermission(false)]
    [RequireContext(ChannelContext.Guild)]
    [Description("Shows Octobot's developers")]
    [UsedImplicitly]
    public async Task<Result> ExecuteAboutAsync()
    {
        if (!_context.TryGetContextIDs(out var guildId, out _, out _))
        {
            return new ArgumentInvalidError(nameof(_context), "Unable to retrieve necessary IDs from command context");
        }

        var botResult = await _userApi.GetCurrentUserAsync(CancellationToken);
        if (!botResult.IsDefined(out var bot))
        {
            return Result.FromError(botResult);
        }

        var cfg = await _guildData.GetSettings(guildId, CancellationToken);
        Messages.Culture = GuildSettings.Language.Get(cfg);

        return await SendAboutBotAsync(bot, guildId, CancellationToken);
    }

    private async Task<Result> SendAboutBotAsync(IUser bot, Snowflake guildId, CancellationToken ct = default)
    {
        var builder = new StringBuilder().Append("### ").AppendLine(Messages.AboutTitleDevelopers);
        foreach (var dev in Developers)
        {
            var guildMemberResult = await _guildApi.GetGuildMemberAsync(
                guildId, dev.Id, ct);
            var tag = guildMemberResult.IsSuccess
                ? $"<@{dev.Id}>"
                : Markdown.Hyperlink($"@{dev.Username}", $"https://github.com/{dev.Username}");

            builder.AppendBulletPointLine($"{tag} — {$"AboutDeveloper@{dev.Username}".Localized()}");
        }

        var embed = new EmbedBuilder()
            .WithSmallTitle(string.Format(Messages.AboutBot, bot.Username), bot)
            .WithDescription(builder.ToString())
            .WithColour(ColorsList.Cyan)
            .WithImageUrl("https://i.ibb.co/fS6wZhh/octobot-banner.png")
            .WithFooter(string.Format(Messages.Version, BuildInfo.Version))
            .Build();

        var repositoryButton = new ButtonComponent(
            ButtonComponentStyle.Link,
            Messages.ButtonOpenRepository,
            new PartialEmoji(Name: "🌐"),
            URL: BuildInfo.RepositoryUrl
        );

        var issuesButton = new ButtonComponent(
            ButtonComponentStyle.Link,
            Messages.ButtonReportIssue,
            new PartialEmoji(Name: "⚠️"),
            URL: BuildInfo.IssuesUrl
        );

        return await _feedback.SendContextualEmbedResultAsync(embed,
            new FeedbackMessageOptions(MessageComponents: new[]
            {
                new ActionRowComponent(new[] { repositoryButton, issuesButton })
            }), ct);
    }
}
