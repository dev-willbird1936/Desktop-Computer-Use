// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Collections.Concurrent;

namespace ShadowUse.Automation;

/// <summary>
/// Process-lifetime named value store (F39). Lets a value read from one app (via
/// capture_workflow_value/browser_capture_value) be entered into another via
/// type_text/set_value/browser_fill's value_ref, without the caller having to
/// repeat the literal text and without Jev ever generating it — Jev only ever
/// chooses among real candidates, never text content.
/// </summary>
public sealed class WorkflowValueStore
{
    private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.Ordinal);

    public void Set(string name, string value)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Workflow value name must not be empty.", nameof(name));
        _values[name] = value;
    }

    public bool TryGet(string name, out string value) => _values.TryGetValue(name, out value!);

    public bool Delete(string name) => _values.TryRemove(name, out _);

    /// <summary>
    /// Resolves exactly one supplied value source: a literal, a stored workflow value name,
    /// or an environment variable name. Throws <see cref="InvalidOperationException"/> if zero
    /// or more than one source was supplied, or the referenced source is unavailable.
    /// </summary>
    public static string Resolve(string? literal, string? valueRef, string? valueEnv, WorkflowValueStore store)
    {
        var suppliedCount = (literal is not null ? 1 : 0) + (valueRef is not null ? 1 : 0) + (valueEnv is not null ? 1 : 0);
        if (suppliedCount == 0)
            throw new InvalidOperationException("Supply exactly one of the value/text, value_ref, or value_env arguments.");
        if (suppliedCount > 1)
            throw new InvalidOperationException("Supply only one of the value/text, value_ref, or value_env arguments.");

        if (literal is not null)
            return literal;

        if (valueRef is not null)
        {
            if (!store.TryGet(valueRef, out var stored))
                throw new InvalidOperationException($"No workflow value named '{valueRef}'. Call set_workflow_value or capture_workflow_value/browser_capture_value first.");
            return stored;
        }

        var env = Environment.GetEnvironmentVariable(valueEnv!);
        if (env is null)
            throw new InvalidOperationException($"Environment variable '{valueEnv}' is not set.");
        return env;
    }
}
