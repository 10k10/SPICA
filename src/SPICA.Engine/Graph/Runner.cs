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
        /// <summary>The block reused earlier results instead of working again.</summary>
        public bool Cached;
        /// <summary>Set by an executor whose work goes on after Run returns (Simulate waits for the queue); it then calls <see cref="Done"/>.</summary>
        public bool Waiting;
        /// <summary>Called by a waiting executor when it is finished: null, or why it failed.</summary>
        public Action<string> Done;

        public List<object> In(string port) => Inputs.TryGetValue(port, out var l) ? l : new List<object>();
        /// <summary>Every item of every value on a port whose values are lists of T.</summary>
        public List<T> All<T>(string port) => In(port).OfType<IEnumerable<T>>().SelectMany(x => x).ToList();
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
    /// A block may wait (Simulate, for its campaigns): the runner stops there and goes on when the block is done.
    /// A block whose kind has no executor yet, or whose inputs failed, is skipped with the reason, and so is what it feeds.
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
        /// <summary>Called once when the whole study has run (or stopped): true when every block finished.</summary>
        public Action<bool> Ended = ok => { };

        Study study;
        List<Block> order;
        int next;
        bool stopped;

        public Study Study => study;
        public bool Busy { get; private set; }
        public bool Waiting { get; private set; }

        public StudyRunner Register(string kind, IBlockExecutor ex) { executors[kind] = ex; return this; }
        public bool Has(string kind) => executors.ContainsKey(kind);

        /// <summary>
        /// Starts the study; false when it cannot start (the issues are logged). With no waiting block it has run to
        /// the end when this returns.
        /// </summary>
        public bool Start(Study s)
        {
            State.Clear(); Message.Clear(); Results.Clear();
            study = s; stopped = false; Waiting = false;
            var issues = s.Validate();
            if (issues.Count > 0)
            {
                foreach (var i in issues) Log("Cannot run: " + i);
                return false;
            }
            order = s.Order();
            foreach (var b in order) State[b.Id] = BlockState.Ready;
            next = 0;
            Busy = true;
            Continue();
            return true;
        }

        /// <summary>Runs the study to the end (no block may wait); true when every block finished.</summary>
        public bool Run(Study s) => Start(s) && !Waiting && Succeeded;

        public bool Succeeded => State.Values.All(x => x == BlockState.Done || x == BlockState.Cached);

        /// <summary>Stops after the block that runs now; what is left is skipped.</summary>
        public void Stop()
        {
            stopped = true;
            if (Waiting) { Waiting = false; Finish(); }
        }

        void Continue()
        {
            while (next < order.Count)
            {
                if (stopped) break;
                var b = order[next++];
                var feeders = study.Wires.Where(w => w.To == b.Id).ToList();
                var bad = feeders.Select(w => w.From).Distinct().Where(id => State.TryGetValue(id, out var s) && s != BlockState.Done && s != BlockState.Cached).ToList();
                if (bad.Count > 0) { Set(b, BlockState.Skipped, "an input block did not finish: " + string.Join(", ", bad.Select(id => study[id]?.Spec?.Title ?? id.ToString()))); continue; }
                if (!executors.TryGetValue(b.Kind, out var ex)) { Set(b, BlockState.Skipped, $"{b.Spec?.Title ?? b.Kind} cannot run yet in this version"); continue; }

                var ctx = new BlockContext { Study = study, Block = b, Log = Log };
                foreach (var w in feeders)
                {
                    if (!Results.TryGetValue(w.From, out var outs) || !outs.TryGetValue(w.FromPort, out var v)) continue;
                    if (!ctx.Inputs.TryGetValue(w.ToPort, out var l)) ctx.Inputs[w.ToPort] = l = new List<object>();
                    l.Add(v);
                }
                Set(b, BlockState.Running, null);
                bool sync = true;
                ctx.Done = why =>
                {
                    Record(ctx, why);
                    if (sync) return;              // finished inside Run: the loop goes on by itself
                    Waiting = false;
                    Continue();
                };
                try
                {
                    ex.Run(ctx);
                }
                catch (Exception e)
                {
                    ctx.Waiting = false;
                    Record(ctx, e.Message);
                    continue;
                }
                sync = false;
                if (ctx.Waiting && State[b.Id] == BlockState.Running) { Waiting = true; return; }
                if (!ctx.Waiting) Record(ctx, null);
            }
            if (stopped) foreach (var b in order.Skip(next)) if (State[b.Id] == BlockState.Ready) Set(b, BlockState.Skipped, "stopped");
            Finish();
        }

        void Record(BlockContext ctx, string why)
        {
            var b = ctx.Block;
            if (State[b.Id] != BlockState.Running) return;     // recorded already
            if (why != null) { Set(b, BlockState.Error, why); return; }
            Results[b.Id] = new Dictionary<string, object>(ctx.Outputs, StringComparer.OrdinalIgnoreCase);
            Set(b, ctx.Cached ? BlockState.Cached : BlockState.Done, null);
        }

        void Finish()
        {
            if (!Busy) return;
            Busy = false;
            Ended(Succeeded);
        }

        void Set(Block b, BlockState s, string why)
        {
            State[b.Id] = s;
            if (why != null) { Message[b.Id] = why; Log($"{b.Spec?.Title ?? b.Kind} ({b.Id}): {s.ToString().ToLowerInvariant()}, {why}"); }
            Changed(b, s);
        }
    }
}
