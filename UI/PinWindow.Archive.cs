namespace Markshot.UI;

public partial class PinWindow
{
    /// <summary>Called after Closing has committed the text/gesture and composed the visible image.</summary>
    public PinArchive CaptureArchive() => PinArchive.CreateSession(new PinArchiveState(Snapshot(), _history,
        CornerRadius, ShapedShadow, new string(_orientation.ToArray()), _untranslatedBase, _translatedBase,
        _translatedText, _pinSourceLabel, _pinTargetLabel, _pinShowsOriginal));

    /// <summary>Before Show: continue the archived undo stack, not just the flattened pixels.</summary>
    public void AdoptArchive(PinArchiveState state)
    {
        AdoptHistory(state.History);
        _orientation.Clear();
        _orientation.AddRange(state.Orientation);
        _untranslatedBase = state.Untranslated;
        _translatedBase = state.Translated;
        _translatedText = state.TranslationText;
        _pinSourceLabel = state.SourceLabel;
        _pinTargetLabel = state.TargetLabel;
        _pinShowsOriginal = state.ShowsOriginal;
        UpdatePinLanguageSwitch();
    }
}
