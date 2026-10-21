namespace Douze.Workflow;

/// <summary>
/// What happens when a unit of work fails.
/// </summary>
internal enum FailurePolicy
{
    /// <summary>The failing step stops; its dependents are skipped; independent steps finish.</summary>
    Default,

    /// <summary>The whole build stops on the first error (<c>--fail-fast</c>).</summary>
    FailFast,

    /// <summary>The failing step processes its other units and reports every error (<c>--keep-going</c>).</summary>
    KeepGoing,
}
