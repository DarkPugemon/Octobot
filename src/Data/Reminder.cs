namespace Octobot.Data;

public struct Reminder
{
    public DateTimeOffset At;
    public string Text;
    public ulong Channel;
}
