﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Statecharts.NET.Model;
using Statecharts.NET.Utilities;
using Action = System.Action;
using Task = System.Threading.Tasks.Task;

namespace Statecharts.NET.Interpreter
{
    internal static class ReadabilityExtensions
    {
        internal static void CancelAll(this Dictionary<StateNode, CancellationTokenSource> cancellationTokens)
            => cancellationTokens.Values.ToList().ForEach(token => token.Cancel());
        internal static IEnumerable<StateNode> GetEnteredStateNodes(this IEnumerable<MicroStep> microSteps)
            => microSteps.SelectMany(step => step.EnteredStateNodes);
        internal static IEnumerable<StateNode> GetExitedStateNodes(this IEnumerable<MicroStep> microSteps)
            => microSteps.SelectMany(step => step.ExitedStateNodes);
    }

    public class RunningStatechart<TContext> where TContext : IEquatable<TContext>
    {
        private readonly ExecutableStatechart<TContext> _statechart;
        private readonly TaskCompletionSource<object> _taskSource;
        private readonly Dictionary<StateNode, CancellationTokenSource> _serviceCancellationTokens;

        private StateConfiguration _stateConfiguration;
        private TContext _context;

        // TODO: config object
        private readonly ILogger _logger = new ConsoleLogger();

        private State<TContext> CurrentState => new State<TContext>(_stateConfiguration, _context);

        public RunningStatechart(ExecutableStatechart<TContext> statechart, CancellationToken cancellationToken)
        {
            _statechart = statechart;
            _taskSource = new TaskCompletionSource<object>();
            _serviceCancellationTokens = new Dictionary<StateNode, CancellationTokenSource>();

            statechart.RegisterDoneAction(CompleteSuccessfully);
            cancellationToken.Register(CompleteCancelled);
        }

        private EventList ExecuteActionBlock(ActionBlock actions)
        {
            var result = EventList.Empty();
            try
            {
                foreach (var action in actions)
                    // TODO: where to get EventData from
                    action.Switch(
                        send => result.AddForNextStep(new NamedEvent(send.EventName)),
                        raise => result.AddForCurrentStep(new NamedEvent(raise.EventName)),
                        log => _logger.Log(log.Message(_context, default)),
                        assign => assign.Mutation(_context, default),
                        sideEffect => sideEffect.Function(_context, default));
            }
            catch (Exception e)
            {
                result.AddForCurrentStep(new ExecutionErrorEvent(e));
            }
            return result;
        }
        private EventList Apply(MicroStep microStep, Func<ActionBlock, EventList> fExecute)
        {
            var events = EventList.Empty();

            EventList ExecuteMultiple(IEnumerable<ActionBlock> actionBlocks) =>
                EventList.From(actionBlocks.SelectMany(fExecute).ToList());
            void ExecuteExitActions() => events.AddRange(ExecuteMultiple(microStep.ExitedActionBlocks));
            void ExecuteTransitionActions() => events.AddRange(fExecute(microStep.TransitionActionBlock));
            void ExecuteEntryActions() => events.AddRange(ExecuteMultiple(microStep.EnteredActionBlocks));
            
            ExecuteExitActions();
            StopServices(microStep.ExitedStateNodes);
            ExecuteTransitionActions();
            ExecuteEntryActions();

            return events;
        }

        public void Send(ISendableEvent sentEvent)
        {
            var events = EventQueue.WithSentEvent(sentEvent);

            IReadOnlyCollection<MicroStep> ResolveMicroSteps(IEvent @event) =>
                _statechart.ResolveSingleEvent(CurrentState, @event).ToList().AsReadOnly();
            void Execute(IEnumerable<MicroStep> microSteps)
            {
                foreach (var step in microSteps)
                    foreach (var @event in Apply(step, ExecuteActionBlock))
                        @event.Switch(events.Enqueue, events.Enqueue);
            }
            void StabilizeIfNecessary(IReadOnlyCollection<MicroStep> microSteps)
            {
                if(microSteps.GetEnteredStateNodes().Any()) events.EnqueueStabilizationEvent();
            }
            void EnqueueDoneEvents(IReadOnlyCollection<MicroStep> microSteps)
            {
                // TODO: check state finishing and enqueue
            }
            void UpdateStateConfiguration(IReadOnlyCollection<MicroStep> microSteps) =>
                _stateConfiguration = _stateConfiguration.Without(microSteps.GetEnteredStateNodes().Ids()).With(microSteps.GetExitedStateNodes().Ids()); // TODO: Ids is ugly
            void UpdateMacroStep(MacroStep macroStep, IEnumerable<MicroStep> microSteps) =>
                macroStep.Add(microSteps);

            while (events.IsNotEmpty)
            {
                var macroStep = new MacroStep();
                while (events.NextIsInternal)
                {
                    var microSteps = ResolveMicroSteps(events.Dequeue());
                    Execute(microSteps);
                    StabilizeIfNecessary(microSteps);
                    EnqueueDoneEvents(microSteps);
                    UpdateStateConfiguration(microSteps);
                    UpdateMacroStep(macroStep, microSteps);
                }
                StartServices(macroStep.GetEnteredStateNodes());
            }
        }

        internal Task StartFrom(State<TContext> state) => Start(
            state.StateConfiguration,
            state.Context,
            () => StartServices()); // TODO: think about this
        internal Task StartFromRootState() => Start(
            new StateConfiguration(_statechart.RootNode.Yield().Ids()),
            _statechart.InitialContext,
            () => Send(InitEvent)); // TODO: generate Init Event Transition when Parsing

        private Task Start(
            StateConfiguration stateConfiguration,
            TContext context,
            System.Action initAction)
        {
            _stateConfiguration = stateConfiguration;
            _context = context.Clone();

            initAction();

            return _taskSource.Task;
        }

        private void StartServices(IEnumerable<StateNode> stateNodes) // TODO: probably make this static
        {
            var entered = stateNodes
                .Select(stateNode => (stateNode, services: stateNode.Services))
                .Where(entry => entry.services.Any());

            foreach (var (stateNode, services) in entered)
            {
                _serviceCancellationTokens.Add(stateNode, new CancellationTokenSource());
                foreach (var service in services)
                    service.Invoke(_serviceCancellationTokens[stateNode].Token).ContinueWith(task =>
                    {
                        switch (task.Status)
                        {
                            case TaskStatus.RanToCompletion:
                                Send(new ServiceSuccessEvent(service.Id, task.Result));
                                break;
                            case TaskStatus.Faulted:
                                Send(new ServiceErrorEvent(service.Id, task.Exception));
                                break;
                        }
                    }, _serviceCancellationTokens[stateNode].Token);
            }
        }
        private void StopServices(IEnumerable<StateNode> stateNodes) // TODO: probably make this static
        {
            foreach (var (stateNode, token) in stateNodes
                .Select(stateNode => (stateNode, cancellationTokenSource: _serviceCancellationTokens.GetValue(stateNode)))
                .Where(o => o.cancellationTokenSource != null))
            {
                token.Cancel();
                _serviceCancellationTokens.Remove(stateNode);
            }
        }
        private void Complete(Action finalAction)
        {
            _serviceCancellationTokens.CancelAll();
            finalAction();
        }
        private void CompleteSuccessfully() => Complete(() => _taskSource.TrySetResult(null));
        private void CompleteCancelled() => Complete(() => _taskSource.TrySetCanceled());
    }
}
