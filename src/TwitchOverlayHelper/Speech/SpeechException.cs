namespace TwitchOverlayHelper.Speech;

/// <summary>Anything that stops a name from being read out loud, phrased for the dock's toast.</summary>
public sealed class SpeechException(string message) : Exception(message);
