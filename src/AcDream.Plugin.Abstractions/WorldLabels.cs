using System.Numerics;

namespace AcDream.Plugin.Abstractions;

/// <summary>
/// One line of text hung in the world over an object, drawn at a constant
/// screen size wherever the object goes.
/// </summary>
/// <param name="ObjectId">
/// The object the label follows, by the id the server gave it. A label with
/// a zero id is dropped.
/// </param>
/// <param name="Text">
/// What the label says. A label with no text is dropped. Text is drawn on
/// one line as given; it is not wrapped.
/// </param>
/// <param name="Color">
/// The text colour, red, green, blue and alpha each from 0 to 1.
/// </param>
/// <param name="HeightOffset">
/// Metres added to the host's own measure of the object's height, so a label
/// can sit a little above the head, or below it with a negative value. The
/// host works the object's height out itself; a plugin never has to.
/// </param>
/// <param name="Line">
/// Which line this is over the object, counting up from zero at the object's
/// head. Two labels on one object with lines 0 and 1 stack, the second above
/// the first, without either plugin knowing the font's size.
/// </param>
/// <param name="MaxRange">
/// How far from the camera, in metres, the label is still drawn. It fades out
/// over the last fifth of that distance. Must be a positive finite number, or
/// the label is dropped.
/// </param>
/// <param name="Outline">
/// True to draw the text with a dark outline so it stays readable over a
/// bright sky or a light wall.
/// </param>
public readonly record struct PluginWorldLabel(
    uint ObjectId,
    string Text,
    Vector4 Color,
    float HeightOffset = 0f,
    int Line = 0,
    float MaxRange = 60f,
    bool Outline = true);

/// <summary>
/// Hanging text labels over objects in the world: name tags, distances,
/// whatever a plugin wants the player to read at a glance.
///
/// <para>The labels are not occluded: a label shows through a wall, a hill
/// or another object, because the interface is drawn after the world with
/// no depth to test against. A plugin that needs a label to disappear with
/// its object behind cover has to decide that itself.</para>
/// </summary>
public interface IWorldLabelAutomation
{
    /// <summary>
    /// The most labels one plugin may have showing at once. A larger set is
    /// refused as a whole rather than trimmed, so a plugin finds out.
    /// </summary>
    const int MaximumLabels = 256;

    /// <summary>
    /// Replaces every label this plugin has showing with the given set. The
    /// set is copied; the list may be reused afterwards. Labels with a zero
    /// object id, no text, or a range that is not a positive finite number
    /// are dropped from the set. An empty set clears the plugin's labels.
    /// </summary>
    /// <param name="labels">The labels to show from now on.</param>
    /// <returns>
    /// True when the set was taken. False when it held more than
    /// <see cref="MaximumLabels"/> entries, in which case the labels already
    /// showing are left as they were, or when this host cannot show labels,
    /// which is what the default implementation always reports.
    /// </returns>
    bool ShowLabels(IReadOnlyList<PluginWorldLabel> labels) => false;
}
