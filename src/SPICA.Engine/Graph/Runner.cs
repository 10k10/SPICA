using System;
using System.Collections.Generic;
using System.Linq;

namespace Spica.Core.Graph
{
    /// <summary>What a block executor sees: its block, the values on its input ports and where its outputs go.</summary>
    public sealed class BlockContext
    {
        public Study Study;
        public Block Block;
        /// <summary>Input port name → the values on it (one per wire, in wire order).</summary>
        public readonly Dictionary<string, List<object>> Inputs = new Dictionary<string, List<object>>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Output port name → the value the block produced.</summary>
        public readonly Dictionary<string, object> Outputs = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        public Action<string> Log = s => { };

        public List<object> In(string port) => Inputs.TryGetValue(port, out var l) ? l : new List<object>();
        public void Out(string port, object value) => Outputs[port] = value;
    }

    /// <summary>The work of one block kind. Simulate turns its inputs into queue campaigns; the others are quick.</summary>
    public interface IBlockExecutor
    {
        void Run(BlockContext ctx);
    }

    public enum BlockState { Ready, Running, Done, Cached, Error, Skipped }

    /// <summary>
    /// Runs a study: validates it, then runs the blocks in wire order, each with the outputs of the blocks that feed it.
    /// A block whose kind has no executor yet, or whose inputs failed, is skipped with the reason.
    /// </summary>
    public sealed class StudyRunner
    {
        readonly Dictionary<string, IBlockExecutor> executors = new Dictionary<string, IBlockExecutor>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<int, BlockState> State = new Dictionary<int, BlockState>();
        public readonly Dictionary<int, string> Message = new Dictionary<int, string>();
        public readonly Dictionary<int, Dictionary<string, object>> Results = new Dictionary<int, Dictionary<string, object>>();
        public Action<string> Log = s => { };
        /// <summary>Called when a block changes state (for the editor's badges).</summary>
        public Action<Block, BlockState> Changed = (b, s) => { };

        public StudyRunner Register(string kind, IBlockExecutor ex) { executors[kind] = ex; return this; }
        public bool Has(string kind) => executors.ContainsKey(kind);

        /// <summary>Runs the study; false when it cannot start (the issues are logged) or a block failed.</summary>
        public bool Run(Study study)
        {
            State.Clear(); Message.Clear(); Results.Clear();
            var issues = study.Validate();
            if (issues.Count > 0)
            {
                foreach (var i in issues) Log("Cannot run: " + i);
                return false;
            }
            bool ok = true;
            foreach (var b in study.Order())
            {
                var feeders = study.Wires.Where(w => w.To == b.Id).ToList();
                var bad = feeders.Select(w => w.From).Distinct().Where(id => State.TryGetValue(id, out var s) && s != BlockState.Done && s != BlockState.Cached).ToList();
                if (bad.Count > 0) { Set(b, BlockState.Skipped, "an input block did not finish: " + string.Join(", ", bad)); continue; }
                if (!executors.TryGetValue(b.Kind, out var ex)) { Set(b, BlockState.Skipped, $"{b.Kind} cannot run yet in this version"); continue; }

                var ctx = new BlockContext { Study = study, Block = b, Log = Log };
                foreach (var w in feeders)
                {
                    if (!Results.TryGetValue(w.From, out var outs) || !outs.TryGetValue(w.FromPort, out var v)) continue;
                    if (!ctx.Inputs.TryGetValue(w.ToPort, out var l)) ctx.Inputs[w.ToPort] = l = new List<object>();
                    l.Add(v);
                }
                Set(b, BlockState.Running, null);
                try
                {
                    ex.Run(ctx);
                    Results[b.Id] = new Dictionary<string, object>(ctx.Outputs, StringComparer.OrdinalIgnoreCase);
                    Set(b, BlockState.Done, null);
                }
                catch (Exception e)
                {
                    ok = false;
                    Set(b, BlockState.Error, e.Message);
                }
            }
            return ok && State.Values.All(s => s == BlockState.Done || s == BlockState.Cached);
        }

        void Set(Block b, BlockState s, string why)
        {
            State[b.Id] = s;
            if (why != null) { Message[b.Id] = why; Log($"{b}: {s.ToString().ToLowerInvariant()}, {why}"); }
            Changed(b, s);
        }
    }
}
