using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Octobot.Data;
using Octobot.Extensions;
using Remora.Discord.API.Abstractions.Objects;
using Remora.Discord.API.Abstractions.Rest;
using Remora.Discord.Extensions.Embeds;
using Remora.Discord.Extensions.Formatting;
using Remora.Rest.Core;
using Remora.Results;

namespace Octobot.Services.Update;

public sealed partial class MemberUpdateService : BackgroundService
{
    private static readonly string[] GenericNicknames =
    [
        "Albatross", "Alpha", "Anchor", "Banjo", "Bell", "Beta", "Blackbird", "Bulldog", "Canary",
        "Cat", "Calf", "Cyclone", "Daisy", "Dalmatian", "Dart", "Delta", "Diamond", "Donkey", "Duck",
        "Emu", "Eclipse", "Flamingo", "Flute", "Frog", "Goose", "Hatchet", "Heron", "Husky", "Hurricane",
        "Iceberg", "Iguana", "Kiwi", "Kite", "Lamb", "Lily", "Macaw", "Manatee", "Maple", "Mask",
        "Nautilus", "Ostrich", "Octopus", "Pelican", "Puffin", "Pyramid", "Rattle", "Robin", "Rose",
        "Salmon", "Seal", "Shark", "Sheep", "Snake", "Sonar", "Stump", "Sparrow", "Toaster", "Toucan",
        "Torus", "Violet", "Vortex", "Vulture", "Wagon", "Whale", "Woodpecker", "Zebra", "Zigzag"
    ];

    private readonly IDiscordRestChannelAPI _channelApi;
    private readonly IDiscordRestGuildAPI _guildApi;
    private readonly GuildDataService _guildData;
    private readonly ILogger<MemberUpdateService> _logger;
    private readonly Utility _utility;

    public MemberUpdateService(IDiscordRestChannelAPI channelApi, IDiscordRestGuildAPI guildApi,
        GuildDataService guildData, ILogger<MemberUpdateService> logger, Utility utility)
    {
        _channelApi = channelApi;
        _guildApi = guildApi;
        _guildData = guildData;
        _logger = logger;
        _utility = utility;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        var tasks = new List<Task>();

        while (await timer.WaitForNextTickAsync(ct))
        {
            var guildIds = _guildData.GetGuildIds();

            tasks.AddRange(guildIds.Select(async id =>
            {
                var tickResult = await TickMemberDatasAsync(id, ct);
                _logger.LogResult(tickResult, $"Error in member data update for guild {id}.");
            }));

            await Task.WhenAll(tasks);
            tasks.Clear();
        }
    }

    private async Task<Result> TickMemberDatasAsync(Snowflake guildId, CancellationToken ct)
    {
        var guildData = await _guildData.GetData(guildId, ct);
        var defaultRole = GuildSettings.DefaultRole.Get(guildData.Settings);
        var failedResults = new List<Result>();
        var memberDatas = guildData.MemberData.Values.ToArray();
        foreach (var data in memberDatas)
        {
            var tickResult = await TickMemberDataAsync(guildId, guildData, defaultRole, data, ct);
            failedResults.AddIfFailed(tickResult);
        }

        return failedResults.AggregateErrors();
    }

    private async Task<Result> TickMemberDataAsync(Snowflake guildId, GuildData guildData, Snowflake defaultRole,
        MemberData data,
        CancellationToken ct)
    {
        var failedResults = new List<Result>();
        var id = data.Id.ToSnowflake();

        var autoUnbanResult = await TryAutoUnbanAsync(guildId, id, data, ct);
        failedResults.AddIfFailed(autoUnbanResult);

        var guildMemberResult = await _guildApi.GetGuildMemberAsync(guildId, id, ct);
        if (!guildMemberResult.IsDefined(out var guildMember))
        {
            return failedResults.AggregateErrors();
        }

        var interactionResult
            = await _utility.CheckInteractionsAsync(guildId, null, id, "Update", ct);
        if (!interactionResult.IsSuccess)
        {
            return Result.FromError(interactionResult);
        }

        var canInteract = interactionResult.Entity is null;

        if (data.MutedUntil is null)
        {
            data.Roles = guildMember.Roles.ToList().ConvertAll(r => r.Value);
        }

        if (!guildMember.User.IsDefined(out var user))
        {
            failedResults.AddIfFailed(new ArgumentNullError(nameof(guildMember.User)));
            return failedResults.AggregateErrors();
        }

        for (var i = data.Reminders.Count - 1; i >= 0; i--)
        {
            var reminderTickResult = await TickReminderAsync(data.Reminders[i], user, data, guildId, ct);
            failedResults.AddIfFailed(reminderTickResult);
        }

        if (!canInteract)
        {
            return Result.FromSuccess();
        }

        var autoUnmuteResult = await TryAutoUnmuteAsync(guildId, id, data, ct);
        failedResults.AddIfFailed(autoUnmuteResult);

        if (!defaultRole.Empty() && !data.Roles.Contains(defaultRole.Value))
        {
            var addResult = await _guildApi.AddGuildMemberRoleAsync(
                guildId, id, defaultRole, ct: ct);
            failedResults.AddIfFailed(addResult);
        }

        if (GuildSettings.RenameHoistedUsers.Get(guildData.Settings))
        {
            var filterResult = await FilterNicknameAsync(guildId, user, guildMember, ct);
            failedResults.AddIfFailed(filterResult);
        }

        return failedResults.AggregateErrors();
    }

    private async Task<Result> TryAutoUnbanAsync(
        Snowflake guildId, Snowflake id, MemberData data, CancellationToken ct)
    {
        if (data.BannedUntil is null || DateTimeOffset.UtcNow <= data.BannedUntil)
        {
            return Result.FromSuccess();
        }

        var existingBanResult = await _guildApi.GetGuildBanAsync(guildId, id, ct);
        if (!existingBanResult.IsDefined())
        {
            data.BannedUntil = null;
            return Result.FromSuccess();
        }

        var unbanResult = await _guildApi.RemoveGuildBanAsync(
            guildId, id, Messages.PunishmentExpired.EncodeHeader(), ct);
        if (unbanResult.IsSuccess)
        {
            data.BannedUntil = null;
        }

        return unbanResult;
    }

    private async Task<Result> TryAutoUnmuteAsync(
        Snowflake guildId, Snowflake id, MemberData data, CancellationToken ct)
    {
        if (data.MutedUntil is null || DateTimeOffset.UtcNow <= data.MutedUntil)
        {
            return Result.FromSuccess();
        }

        var unmuteResult = await _guildApi.ModifyGuildMemberAsync(
            guildId, id, roles: data.Roles.ConvertAll(r => r.ToSnowflake()),
            reason: Messages.PunishmentExpired.EncodeHeader(), ct: ct);
        if (unmuteResult.IsSuccess)
        {
            data.MutedUntil = null;
        }

        return unmuteResult;
    }

    private async Task<Result> FilterNicknameAsync(Snowflake guildId, IUser user, IGuildMember member,
        CancellationToken ct)
    {
        var currentNickname = member.Nickname.IsDefined(out var nickname)
            ? nickname
            : user.GlobalName.OrDefault(user.Username);
        var characterList = currentNickname.ToList();
        var usernameChanged = false;
        foreach (var character in currentNickname)
        {
            if (IllegalChars().IsMatch(character.ToString()))
            {
                characterList.Remove(character);
                usernameChanged = true;
                continue;
            }

            break;
        }

        if (!usernameChanged)
        {
            return Result.FromSuccess();
        }

        var newNickname = string.Concat(characterList.ToArray());

        return await _guildApi.ModifyGuildMemberAsync(
            guildId, user.ID,
            !string.IsNullOrWhiteSpace(newNickname)
                ? newNickname
                : GenericNicknames[Random.Shared.Next(GenericNicknames.Length)],
            ct: ct);
    }

    [GeneratedRegex("[^0-9A-Za-zА-Яа-яЁё]")]
    private static partial Regex IllegalChars();

    private async Task<Result> TickReminderAsync(Reminder reminder, IUser user, MemberData data, Snowflake guildId,
        CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow < reminder.At)
        {
            return Result.FromSuccess();
        }

        var builder = new StringBuilder()
            .AppendBulletPointLine(string.Format(Messages.DescriptionReminder, Markdown.InlineCode(reminder.Text)))
            .AppendBulletPointLine(string.Format(Messages.DescriptionActionJumpToMessage, $"https://discord.com/channels/{guildId.Value}/{reminder.ChannelId}/{reminder.MessageId}"));

        var embed = new EmbedBuilder().WithSmallTitle(
                string.Format(Messages.Reminder, user.GetTag()), user)
            .WithDescription(builder.ToString())
            .WithColour(ColorsList.Magenta)
            .Build();

        var messageResult = await _channelApi.CreateMessageWithEmbedResultAsync(
            reminder.ChannelId.ToSnowflake(), Mention.User(user), embedResult: embed, ct: ct);
        if (!messageResult.IsSuccess)
        {
            return messageResult;
        }

        data.Reminders.Remove(reminder);
        return Result.FromSuccess();
    }
}
