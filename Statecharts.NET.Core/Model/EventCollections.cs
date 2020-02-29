﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Statecharts.NET.Utilities;

namespace Statecharts.NET.Model
{
    internal interface IQueuedEvent
    {
        IEvent Event { get; }
        int Priority { get; } // lower values have a higher priority
    }
    internal class Stabilization : IQueuedEvent
    {
        public IEvent Event { get; }
        public int Priority => 1;
        public Stabilization(IEvent @event) => Event = @event;
    }
    internal class CurrentStep : IQueuedEvent
    {
        public IEvent Event { get; }
        public int Priority => 2;
        public CurrentStep(IEvent @event) => Event = @event;
    }
    internal class NextStep : IQueuedEvent
    {
        public IEvent Event { get; }
        public int Priority => 3;
        public NextStep(IEvent @event) => Event = @event;
    }

    internal class EventQueue
    {
        private readonly PriorityQueue<OneOfUnion<IQueuedEvent, Stabilization, CurrentStep, NextStep>, int> _queue
            = new PriorityQueue<OneOfUnion<IQueuedEvent, Stabilization, CurrentStep, NextStep>, int>();

        private EventQueue() { }

        public static EventQueue WithSentEvent(ISendableEvent initialEvent)
        {
            var eventQueue = new EventQueue();
            eventQueue.Enqueue(new NextStep(initialEvent));
            return eventQueue;
        }
        public IEvent Dequeue() => _queue.Dequeue().AsBase().Event;
        public void EnqueueStabilizationEvent()
        {
            var @event = new Stabilization(new InitializeEvent());
            _queue.Enqueue(@event, @event.Priority);
        }
        public void Enqueue(CurrentStep @event) => _queue.Enqueue(@event, @event.Priority);
        public void Enqueue(NextStep @event) => _queue.Enqueue(@event, @event.Priority);
        public bool IsNotEmpty => _queue.Any();
        public bool NextIsInternal => _queue
            .Skip(1)
            .FirstOrDefault()
            .ToOption()
            .Map(queuedEvent => queuedEvent.Match(stabilization => true, currentStep => true, nextStep => false))
            .ValueOr(true);
    }
    internal class EventList : IEnumerable<OneOf<CurrentStep, NextStep>>
    {
        private readonly List<OneOf<CurrentStep, NextStep>> _events;

        private EventList(IEnumerable<OneOf<CurrentStep, NextStep>> events = null) =>
            _events = (events ?? Enumerable.Empty<OneOf<CurrentStep, NextStep>>()).ToList();

        public static EventList Empty() => new EventList();
        public static EventList From(IEnumerable<OneOf<CurrentStep, NextStep>> events) => new EventList(events);

        public void AddForCurrentStep(IEvent @event) => _events.Add(new CurrentStep(@event));
        public void AddForNextStep(IEvent @event) => _events.Add(new NextStep(@event));
        public void AddRange(IEnumerable<OneOf<CurrentStep, NextStep>> events) => _events.AddRange(events);

        public IEnumerator<OneOf<CurrentStep, NextStep>> GetEnumerator() => _events.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}