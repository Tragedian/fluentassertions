using System;
using System.Linq;
using System.Threading;
using FluentAssertions.Common;

namespace FluentAssertions.Execution
{
    /// <summary>
    /// Represents an implicit or explicit scope within which multiple assertions can be collected.
    /// </summary>
    /// <remarks>
    /// This class is supposed to have a very short life time and is not safe to be used in assertion that cross thread-boundaries such as when
    /// using <c>async</c> or <c>await</c>.
    /// </remarks>
    public sealed class AssertionScope : IAssertionScope
    {
        #region Private Definitions

        private readonly IAssertionStrategy assertionStrategy;
        private readonly ContextDataItems contextData = new ContextDataItems();

        private Func<string> reason;
        private bool useLineBreaks;

        private static readonly AsyncLocal<AssertionScope> CurrentScope = new AsyncLocal<AssertionScope>();
        private AssertionScope parent;
        private Func<string> expectation;
        private string fallbackIdentifier = "object";
        private bool? succeeded;

        #endregion

        /// <summary>
        /// Starts a new scope based on the given assertion strategy and parent assertion scope
        /// </summary>
        /// <param name="assertionStrategy">The assertion strategy for this scope.</param>
        /// <param name="parent">The parent assertion scope for this scope.</param>
        /// <exception cref="ArgumentNullException">Thrown when trying to use a null strategy.</exception>
        internal AssertionScope(IAssertionStrategy assertionStrategy, AssertionScope parent)
        {
            this.assertionStrategy = assertionStrategy
                ?? throw new ArgumentNullException(nameof(assertionStrategy));
            this.parent = parent;
        }

        /// <summary>
        /// Starts a new scope based on the given assertion strategy.
        /// </summary>
        /// <param name="assertionStrategy">The assertion strategy for this scope.</param>
        /// <exception cref="ArgumentNullException">Thrown when trying to use a null strategy.</exception>
        public AssertionScope(IAssertionStrategy assertionStrategy)
            : this(assertionStrategy, GetCurrentAssertionScope())
        {
            SetCurrentAssertionScope(this);

            if (parent != null)
            {
                contextData.Add(parent.contextData);
                Context = parent.Context;
            }
        }

        /// <summary>
        /// Starts an unnamed scope within which multiple assertions can be executed
        /// and which will not throw until the scope is disposed.
        /// </summary>
        public AssertionScope()
            : this(new CollectingAssertionStrategy())
        {
        }

        /// <summary>
        /// Starts a named scope within which multiple assertions can be executed and which will not throw until the scope is disposed.
        /// </summary>
        public AssertionScope(string context)
            : this()
        {
            Context = context;
        }

        /// <summary>
        /// Gets or sets the context of the current assertion scope, e.g. the path of the object graph
        /// that is being asserted on.
        /// </summary>
        public string Context { get; set; }

        /// <summary>
        /// Gets the current thread-specific assertion scope.
        /// </summary>
        public static AssertionScope Current
        {
#pragma warning disable CA2000 // AssertionScope should not be disposed here
            get => GetCurrentAssertionScope() ?? new AssertionScope(new DefaultAssertionStrategy(), parent: null);
#pragma warning restore CA2000
            private set => SetCurrentAssertionScope(value);
        }

        public AssertionScope UsingLineBreaks
        {
            get
            {
                useLineBreaks = true;
                return this;
            }
        }

        public bool Succeeded
        {
            get => succeeded == true;
        }

        public AssertionScope BecauseOf(string because, params object[] becauseArgs)
        {
            reason = () =>
            {
                try
                {
                    string becauseOrEmpty = because ?? string.Empty;
                    return (becauseArgs?.Any() == true) ? string.Format(becauseOrEmpty, becauseArgs) : becauseOrEmpty;
                }
                catch (FormatException formatException)
                {
                    return $"**WARNING** because message '{because}' could not be formatted with string.Format{Environment.NewLine}{formatException.StackTrace}";
                }
            };
            return this;
        }

        /// <summary>
        /// Sets the expectation part of the failure message when the assertion is not met.
        /// </summary>
        /// <remarks>
        /// In addition to the numbered <see cref="string.Format(string,object[])"/>-style placeholders, messages may contain a few
        /// specialized placeholders as well. For instance, {reason} will be replaced with the reason of the assertion as passed
        /// to <see cref="BecauseOf"/>. Other named placeholders will be replaced with the <see cref="Current"/> scope data
        /// passed through <see cref="AddNonReportable"/> and <see cref="AddReportable"/>. Finally, a description of the
        /// current subject can be passed through the {context:description} placeholder. This is used in the message if no
        /// explicit context is specified through the <see cref="AssertionScope"/> constructor.
        /// Note that only 10 <paramref name="args"/> are supported in combination with a {reason}.
        /// If an expectation was set through a prior call to <see cref="WithExpectation"/>, then the failure message is appended to that
        /// expectation.
        /// </remarks>
        /// <param name="message">The format string that represents the failure message.</param>
        /// <param name="args">Optional arguments to any numbered placeholders.</param>
        public AssertionScope WithExpectation(string message, params object[] args)
        {
            Func<string> localReason = reason;
            expectation = () =>
            {
                var messageBuilder = new MessageBuilder(useLineBreaks);
                string reason = localReason?.Invoke() ?? string.Empty;
                string identifier = GetIdentifier();

                return messageBuilder.Build(message, args, reason, contextData, identifier, fallbackIdentifier);
            };

            return this;
        }

        internal void TrackComparands(object subject, object expectation)
        {
            contextData.Add(new ContextDataItems.DataItem("subject", subject, reportable: false, requiresFormatting: true));
            contextData.Add(new ContextDataItems.DataItem("expectation", expectation, reportable: false, requiresFormatting: true));
        }

        public Continuation ClearExpectation()
        {
            expectation = null;

            // SMELL: Isn't this always going to return null? Or this method also called without FailWidth (which sets the success state to null)
            return new Continuation(this, Succeeded);
        }

        public GivenSelector<T> Given<T>(Func<T> selector)
        {
            return new GivenSelector<T>(selector, this, continueAsserting: !succeeded.HasValue || succeeded.Value);
        }

        public AssertionScope ForCondition(bool condition)
        {
            succeeded = condition;

            return this;
        }

        /// <summary>
        /// Makes assertion fail when <paramref name="actualOccurrences"/> does not match <paramref name="constraint"/>.
        /// The occurrence description in natural language could then be inserted in failure message by using {expectedOccurrence} placeholder in
        /// message parameters of <see cref="FluentAssertions.Execution.AssertionScope.FailWith(string, object[])"/> and its overloaded versions.
        /// </summary>
        /// <param name="constraint"><see cref="OccurrenceConstraint"/> defining the number of expected occurrences.</param>
        /// <param name="actualOccurrences">The number of actual occurrences.</param>
        public AssertionScope ForConstraint(OccurrenceConstraint constraint, int actualOccurrences)
        {
            constraint.RegisterReportables(this);
            succeeded = constraint.Assert(actualOccurrences);

            return this;
        }

        public Continuation FailWith(Func<FailReason> failReasonFunc)
        {
            return FailWith(() =>
            {
                string localReason = reason?.Invoke() ?? string.Empty;
                var messageBuilder = new MessageBuilder(useLineBreaks);
                string identifier = GetIdentifier();
                FailReason failReason = failReasonFunc();
                string result = messageBuilder.Build(failReason.Message, failReason.Args, localReason, contextData, identifier, fallbackIdentifier);
                return result;
            });
        }

        internal Continuation FailWithPreFormatted(string formattedFailReason) =>
            FailWith(() => formattedFailReason);

        private Continuation FailWith(Func<string> failReasonFunc)
        {
            try
            {
                bool failed = !succeeded.HasValue || !succeeded.Value;
                if (failed)
                {
                    string result = failReasonFunc();

                    if (expectation != null)
                    {
                        result = expectation() + result;
                    }

                    assertionStrategy.HandleFailure(result.Capitalize());

                    succeeded = false;
                }

                return new Continuation(this, continueAsserting: !failed);
            }
            finally
            {
                succeeded = null;
            }
        }

        /// <summary>
        /// Registers a failure message that does not contain any formatting placeholders.
        /// </summary>
        public Continuation FailWith(string message)
        {
            return FailWith(() => new FailReason(message, new object[0]));
        }

        /// <summary>
        /// Registers a failure message with optional formatting arguments.
        /// </summary>
        public Continuation FailWith(string message, params object[] args)
        {
            return FailWith(() => new FailReason(message, args));
        }

        /// <summary>
        /// Registers a failure message, but postpones evaluation of the formatting arguments until the assertion really fails.
        /// </summary>
        public Continuation FailWith(string message, params Func<object>[] argProviders)
        {
            return FailWith(() => new FailReason(message,
                argProviders.Select(a => a()).ToArray()));
        }

        private string GetIdentifier()
        {
            if (!string.IsNullOrEmpty(Context))
            {
                return Context;
            }

            return CallerIdentifier.DetermineCallerIdentity();
        }

        /// <summary>
        /// Adds a pre-formatted failure message to the current scope.
        /// </summary>
        public void AddPreFormattedFailure(string formattedFailureMessage)
        {
            assertionStrategy.HandleFailure(formattedFailureMessage);
        }

        /// <summary>
        /// Tracks a keyed object in the current scope that is excluded from the failure message in case an assertion fails.
        /// </summary>
        public void AddNonReportable(string key, object value)
        {
            contextData.Add(new ContextDataItems.DataItem(key, value, reportable: false, requiresFormatting: false));
        }

        /// <summary>
        /// Adds some information to the assertion scope that will be included in the message
        /// that is emitted if an assertion fails.
        /// </summary>
        public void AddReportable(string key, string value)
        {
            contextData.Add(new ContextDataItems.DataItem(key, value, reportable: true, requiresFormatting: false));
        }

        public string[] Discard()
        {
            return assertionStrategy.DiscardFailures().ToArray();
        }

        public bool HasFailures()
        {
            return assertionStrategy.FailureMessages.Any();
        }

        /// <summary>
        /// Gets data associated with the current scope and identified by <paramref name="key"/>.
        /// </summary>
        public T Get<T>(string key)
        {
            return contextData.Get<T>(key);
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            SetCurrentAssertionScope(parent);

            if (parent != null)
            {
                foreach (string failureMessage in assertionStrategy.FailureMessages)
                {
                    parent.assertionStrategy.HandleFailure(failureMessage);
                }

                parent = null;
            }
            else
            {
                assertionStrategy.ThrowIfAny(contextData.GetReportable());
            }
        }

        public AssertionScope WithDefaultIdentifier(string identifier)
        {
            fallbackIdentifier = identifier;
            return this;
        }

        private static AssertionScope GetCurrentAssertionScope()
        {
            return CurrentScope.Value;
        }

        private static void SetCurrentAssertionScope(AssertionScope scope)
        {
            CurrentScope.Value = scope;
        }

        #region Explicit Implementation to support the interface

        IAssertionScope IAssertionScope.ForCondition(bool condition) => ForCondition(condition);

        IAssertionScope IAssertionScope.BecauseOf(string because, params object[] becauseArgs) => BecauseOf(because, becauseArgs);

        IAssertionScope IAssertionScope.WithExpectation(string message, params object[] args) => WithExpectation(message, args);

        IAssertionScope IAssertionScope.WithDefaultIdentifier(string identifier) => WithDefaultIdentifier(identifier);

        IAssertionScope IAssertionScope.UsingLineBreaks => UsingLineBreaks;

        #endregion
    }
}
