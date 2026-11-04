namespace ClaudeMemory.Core.Model;

/// <summary>Tool name plus its one-line argument summary (claude_memory.py:583).</summary>
public readonly record struct ToolCall(string Name, string Brief);
