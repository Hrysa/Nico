using System.ComponentModel;

namespace Engine.UI;

/// <summary>Selects how model and retained target values synchronize.</summary>
public enum UIBindingMode
{
    /// <summary>Copies the source value once without retaining subscriptions.</summary>
    OneTime,

    /// <summary>Copies source changes to the retained target.</summary>
    OneWay,

    /// <summary>Copies changes in both directions using an explicit target event.</summary>
    TwoWay
}

/// <summary>Creates deterministic disposable bindings without reflection in update paths.</summary>
public static class UIBinding
{
    /// <summary>Binds an explicit observable source to one retained target.</summary>
    /// <typeparam name="TSource">Observable model type.</typeparam>
    /// <typeparam name="TValue">Transferred value type.</typeparam>
    /// <param name="source">Explicit observable model.</param>
    /// <param name="target">Retained target owning dispatcher affinity.</param>
    /// <param name="sourcePropertyName">Property name used to filter model notifications.</param>
    /// <param name="readSource">Allocation-free model getter.</param>
    /// <param name="writeTarget">Target property setter.</param>
    /// <param name="mode">Binding direction.</param>
    /// <param name="writeSource">Model setter required by two-way binding.</param>
    /// <param name="subscribeTarget">Target-event subscription required by two-way binding.</param>
    /// <param name="unsubscribeTarget">Matching target-event unsubscription.</param>
    /// <returns>Binding lifetime that deterministically releases every subscription.</returns>
    public static IDisposable Bind<TSource, TValue>(
        TSource source,
        UIElement target,
        string sourcePropertyName,
        Func<TSource, TValue> readSource,
        Action<TValue> writeTarget,
        UIBindingMode mode = UIBindingMode.OneWay,
        Action<TSource, TValue>? writeSource = null,
        Action<Action<TValue>>? subscribeTarget = null,
        Action<Action<TValue>>? unsubscribeTarget = null)
        where TSource : class, INotifyPropertyChanged
    {
        ArgumentNullException.ThrowIfNull(source);
        return new BindingSubscription<TSource, TValue>(
            target, sourcePropertyName, readSource, writeTarget, mode,
            writeSource, subscribeTarget, unsubscribeTarget, source, useDataContext: false);
    }

    /// <summary>Binds the target's inherited data context and rebinds when that context changes.</summary>
    /// <typeparam name="TSource">Required observable data-context type.</typeparam>
    /// <typeparam name="TValue">Transferred value type.</typeparam>
    /// <param name="target">Retained target inheriting application data.</param>
    /// <param name="sourcePropertyName">Property name used to filter model notifications.</param>
    /// <param name="readSource">Allocation-free model getter.</param>
    /// <param name="writeTarget">Target property setter.</param>
    /// <param name="mode">Binding direction.</param>
    /// <param name="writeSource">Model setter required by two-way binding.</param>
    /// <param name="subscribeTarget">Target-event subscription required by two-way binding.</param>
    /// <param name="unsubscribeTarget">Matching target-event unsubscription.</param>
    /// <returns>Binding lifetime that releases source, context, and target subscriptions.</returns>
    public static IDisposable BindDataContext<TSource, TValue>(
        UIElement target,
        string sourcePropertyName,
        Func<TSource, TValue> readSource,
        Action<TValue> writeTarget,
        UIBindingMode mode = UIBindingMode.OneWay,
        Action<TSource, TValue>? writeSource = null,
        Action<Action<TValue>>? subscribeTarget = null,
        Action<Action<TValue>>? unsubscribeTarget = null)
        where TSource : class, INotifyPropertyChanged
    {
        return new BindingSubscription<TSource, TValue>(
            target, sourcePropertyName, readSource, writeTarget, mode,
            writeSource, subscribeTarget, unsubscribeTarget, null, useDataContext: true);
    }

    /// <summary>Owns one reflection-free binding and all associated event subscriptions.</summary>
    /// <typeparam name="TSource">Observable model type.</typeparam>
    /// <typeparam name="TValue">Transferred value type.</typeparam>
    private sealed class BindingSubscription<TSource, TValue> : IDisposable
        where TSource : class, INotifyPropertyChanged
    {
        private readonly UIElement _target;
        private readonly string _sourcePropertyName;
        private readonly Func<TSource, TValue> _readSource;
        private readonly Action<TValue> _writeTarget;
        private readonly UIBindingMode _mode;
        private readonly Action<TSource, TValue>? _writeSource;
        private readonly Action<Action<TValue>>? _unsubscribeTarget;
        private readonly bool _useDataContext;
        private TSource? _source;
        private int _generation;
        private bool _updatingTarget;
        private bool _updatingSource;
        private volatile bool _disposed;

        /// <summary>Creates and activates one binding.</summary>
        /// <param name="target">Retained target.</param>
        /// <param name="sourcePropertyName">Filtered source property.</param>
        /// <param name="readSource">Source getter.</param>
        /// <param name="writeTarget">Target setter.</param>
        /// <param name="mode">Binding direction.</param>
        /// <param name="writeSource">Optional source setter.</param>
        /// <param name="subscribeTarget">Optional target-event subscription.</param>
        /// <param name="unsubscribeTarget">Optional target-event unsubscription.</param>
        /// <param name="source">Optional explicit source.</param>
        /// <param name="useDataContext">Whether source follows inherited data context.</param>
        public BindingSubscription(
            UIElement target,
            string sourcePropertyName,
            Func<TSource, TValue> readSource,
            Action<TValue> writeTarget,
            UIBindingMode mode,
            Action<TSource, TValue>? writeSource,
            Action<Action<TValue>>? subscribeTarget,
            Action<Action<TValue>>? unsubscribeTarget,
            TSource? source,
            bool useDataContext)
        {
            ArgumentNullException.ThrowIfNull(target);
            ArgumentException.ThrowIfNullOrWhiteSpace(sourcePropertyName);
            ArgumentNullException.ThrowIfNull(readSource);
            ArgumentNullException.ThrowIfNull(writeTarget);
            if (mode == UIBindingMode.TwoWay
                && (writeSource is null || subscribeTarget is null || unsubscribeTarget is null))
                throw new ArgumentException(
                    "Two-way binding requires source writing and matching target subscriptions.");
            _target = target;
            _sourcePropertyName = sourcePropertyName;
            _readSource = readSource;
            _writeTarget = writeTarget;
            _mode = mode;
            _writeSource = writeSource;
            _unsubscribeTarget = unsubscribeTarget;
            _useDataContext = useDataContext;
            if (mode == UIBindingMode.TwoWay)
                subscribeTarget!(OnTargetChanged);
            if (useDataContext && mode != UIBindingMode.OneTime)
                target.DataContextChanged += OnDataContextChanged;
            AttachSource(useDataContext ? target.DataContext as TSource : source);
            target.RegisterBinding(this);
        }

        /// <summary>Rebinds after the target inherits a different application model.</summary>
        /// <param name="context">New inherited data context.</param>
        private void OnDataContextChanged(object? context) => AttachSource(context as TSource);

        /// <summary>Attaches one model and updates the target immediately.</summary>
        /// <param name="source">Compatible model, or null.</param>
        private void AttachSource(TSource? source)
        {
            if (ReferenceEquals(_source, source))
                return;
            if (_source is not null && _mode != UIBindingMode.OneTime)
                _source.PropertyChanged -= OnSourcePropertyChanged;
            _source = source;
            var generation = Interlocked.Increment(ref _generation);
            if (_source is null)
                return;
            if (_mode != UIBindingMode.OneTime)
                _source.PropertyChanged += OnSourcePropertyChanged;
            RefreshTarget(generation);
        }

        /// <summary>Filters observable model notifications and schedules target refresh.</summary>
        /// <param name="sender">Observable model.</param>
        /// <param name="args">Changed property data.</param>
        private void OnSourcePropertyChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (_disposed || _updatingSource || (!string.IsNullOrEmpty(args.PropertyName)
                && args.PropertyName != _sourcePropertyName))
                return;
            RefreshTarget(Interlocked.Increment(ref _generation));
        }

        /// <summary>Writes one target-originated value back to the active model.</summary>
        /// <param name="value">New retained target value.</param>
        private void OnTargetChanged(TValue value)
        {
            if (_disposed || _updatingTarget || _source is null || _writeSource is null)
                return;
            _updatingSource = true;
            try
            {
                _writeSource(_source, value);
            }
            finally
            {
                _updatingSource = false;
            }
        }

        /// <summary>Writes the newest model value on the target's owning UI thread.</summary>
        /// <param name="generation">Source generation represented by this refresh.</param>
        private void RefreshTarget(int generation)
        {
            var dispatcher = _target.Dispatcher;
            if (dispatcher is not null && !dispatcher.CheckAccess())
            {
                try
                {
                    dispatcher.Post(() => ApplyTargetValue(generation));
                }
                catch (ObjectDisposedException)
                {
                    Dispose();
                }
                return;
            }
            ApplyTargetValue(generation);
        }

        /// <summary>Applies a source value when the scheduled generation remains current.</summary>
        /// <param name="generation">Scheduled source generation.</param>
        private void ApplyTargetValue(int generation)
        {
            if (_disposed || generation != Volatile.Read(ref _generation) || _source is null)
                return;
            _updatingTarget = true;
            try
            {
                _writeTarget(_readSource(_source));
            }
            finally
            {
                _updatingTarget = false;
            }
        }

        /// <summary>Releases model, context, and target event subscriptions.</summary>
        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            Interlocked.Increment(ref _generation);
            if (_source is not null && _mode != UIBindingMode.OneTime)
                _source.PropertyChanged -= OnSourcePropertyChanged;
            if (_useDataContext && _mode != UIBindingMode.OneTime)
                _target.DataContextChanged -= OnDataContextChanged;
            if (_mode == UIBindingMode.TwoWay)
                _unsubscribeTarget!(OnTargetChanged);
            _source = null;
            _target.UnregisterBinding(this);
        }
    }
}
