using Android.Content;

namespace UpsTools.Droid.Platform;

/// <summary>
/// Tells the apps on the phone of each event, for Tasker, MacroDroid, Automate and the like: a broadcast,
/// <see cref="Action"/>, with the event's fields as extras (<c>event</c>, <c>ups</c>, <c>grid</c>,
/// <c>batteryPercent</c>…; see <see cref="EventMessage.Fields"/>) and all of them as JSON in <c>json</c>.
/// </summary>
internal sealed class EventBroadcast(Context context) : IEventBroadcast
{
    public const string Action = "com.dkozlov.upstools.EVENT";

    public void Send(EventMessage message)
    {
        var intent = new Intent(Action);
        foreach (var (name, value) in message.Fields)
        {
            switch (value)
            {
                case bool flag: intent.PutExtra(name, flag); break;
                case long number: intent.PutExtra(name, number); break;
                default: intent.PutExtra(name, value.ToString()); break;
            }
        }
        intent.PutExtra("json", message.ToJson());
        context.SendBroadcast(intent);
    }
}
