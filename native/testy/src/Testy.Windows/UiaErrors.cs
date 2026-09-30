using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace Testy.Windows;

/// <summary>Classifies UI Automation failures that mean "this element is gone or virtualized right now".</summary>
internal static class UiaErrors
{
    /// <summary>UIA_E_ELEMENTNOTAVAILABLE.</summary>
    internal const int ElementNotAvailable = unchecked((int)0x80040201);

    /// <summary>
    /// True for an element that disappeared, was virtualized or had its container recycled while it was being read.
    /// The managed UIA client raises this as <see cref="ElementNotAvailableException"/> when a call fails, but a property read that
    /// returns the provider's error marker (for example a cached property of a recycled WPF row) surfaces as a
    /// <see cref="COMException"/> carrying UIA_E_ELEMENTNOTAVAILABLE and the provider's message. Both are the same transient condition.
    /// </summary>
    internal static bool IsElementUnavailable(Exception exception) =>
        exception is ElementNotAvailableException || exception is COMException { HResult: ElementNotAvailable };
}
