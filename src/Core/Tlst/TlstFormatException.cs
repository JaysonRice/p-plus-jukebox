namespace PPlusTracklistEditor.Core.Tlst;

/// <summary>Thrown when a .tlst file can't be read or written (corrupt file, or too much data for the format).</summary>
public sealed class TlstFormatException(string message) : Exception(message);
