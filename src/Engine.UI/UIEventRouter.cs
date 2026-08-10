using System.Numerics;
using Engine.Core;
using Engine.Graphics;

namespace Engine.UI;

/// <summary>
/// Owns UI hit testing, hover, press, focus, and input-event dispatch.
/// </summary>
public sealed class UIEventRouter
{
    private UIElement _root;
    private readonly Action _invalidate;
    private readonly Action<UIElement> _capturePointerAction;
    private readonly Action _releasePointerCaptureAction;
    private readonly Action<UIElement> _focusAction;
    private readonly IClipboardService? _clipboard;
    private UIElement? _pressedElement;
    private UIElement? _capturedElement;
    private UIElement? _dragCandidate;
    private UIElement? _dragSource;
    private UIElement? _dropTarget;
    private UIDragData? _dragData;
    private UIDragEffect _allowedDragEffects;
    private UIDragEffect _dragEffect;
    private Vector2 _dragStartPosition;
    private bool _completingDrag;
    private readonly List<RouteDispatchState> _routeStates = [];
    private readonly List<UIElement> _focusTraversal = [];
    private int _routeDepth;
    private Vector2 _pointerPosition;
    private UIElement? _cachedInputScope;
    private long _cachedInputScopeVersion = -1;

    /// <summary>Gets the element currently under the pointer.</summary>
    public UIElement? HoveredElement { get; private set; }

    /// <summary>Gets the element that currently owns keyboard focus.</summary>
    public UIElement? FocusedElement { get; private set; }

    /// <summary>Gets the element currently receiving captured pointer input.</summary>
    public UIElement? CapturedElement => _capturedElement;

    /// <summary>Gets whether a drag operation is currently active.</summary>
    public bool IsDragging => _dragSource is not null;

    /// <summary>Gets or sets the movement in logical pixels required to start an automatic drag.</summary>
    public float DragThreshold { get; set; } = 4f;

    /// <summary>Gets the source of the active drag, or null.</summary>
    public UIElement? DragSource => _dragSource;

    /// <summary>Gets the typed data carried by the active drag, or null.</summary>
    public UIDragData? ActiveDragData => _dragData;

    /// <summary>Gets the current accepted drop target, or null.</summary>
    public UIElement? DropTarget => _dropTarget;

    /// <summary>Gets the effect currently accepted by the drop target.</summary>
    public UIDragEffect DragEffect => _dragEffect;

    /// <summary>Gets the latest host-relative pointer position.</summary>
    public Vector2 PointerPosition => _pointerPosition;

    /// <summary>Occurs after active drag state or its target position changes.</summary>
    public event Action? DragStateChanged;

    /// <summary>Occurs after hover, focus, capture, or routed-root diagnostic state changes.</summary>
    public event Action? DiagnosticStateChanged;

    /// <summary>
    /// Creates a router for one UI tree.
    /// </summary>
    /// <param name="root">Root UI element.</param>
    /// <param name="invalidate">Callback used when visual state changes.</param>
    /// <param name="clipboard">Optional host-local text clipboard.</param>
    public UIEventRouter(UIElement root, Action invalidate, IClipboardService? clipboard = null)
    {
        _root = root;
        _invalidate = invalidate;
        _clipboard = clipboard;
        _capturePointerAction = CapturePointer;
        _releasePointerCaptureAction = ReleasePointerCapture;
        _focusAction = Focus;
    }

    /// <summary>Replaces the routed UI tree and clears transient state.</summary>
    /// <param name="root">New root UI element.</param>
    public void SetRoot(UIElement root)
    {
        CancelDrag();
        ReleasePointerCapture();
        _pressedElement?.SetPressed(false);
        HoveredElement?.SetHover(false);
        FocusedElement?.SetFocus(false);
        _pressedElement = null;
        HoveredElement = null;
        FocusedElement = null;
        _root = root;
        _cachedInputScope = null;
        _cachedInputScopeVersion = -1;
        DiagnosticStateChanged?.Invoke();
    }

    /// <summary>Updates the element under the pointer.</summary>
    /// <param name="position">Pointer position in window pixels.</param>
    public void MovePointer(Vector2 position)
    {
        var delta = position - _pointerPosition;
        RoutePointerMove(new PointerMoveEvent(
            0, position, delta, PointerDeviceKind.Mouse,
            InputModifiers.None, PointerButtons.None));
    }

    /// <summary>Routes device-neutral pointer movement through the current UI tree.</summary>
    /// <param name="pointerEvent">Pointer movement to route.</param>
    public void RoutePointerMove(PointerMoveEvent pointerEvent)
    {
        _root.InvalidateTimeUpdateActivity();
        EnsurePointerCaptureValid();
        _pointerPosition = pointerEvent.Position;
        var hit = HitTest(GetInputScope(), pointerEvent.Position, null);
        UpdateHoveredElement(hit);
        if (_dragSource is not null)
        {
            UpdateDrag(hit, pointerEvent.Position);
            return;
        }
        if (_dragCandidate is not null &&
            Vector2.DistanceSquared(_dragStartPosition, pointerEvent.Position) >= DragThreshold * DragThreshold)
        {
            StartDrag(_dragCandidate, _dragCandidate.DragData!, _dragCandidate.AllowedDragEffects);
            UpdateDrag(hit, pointerEvent.Position);
            return;
        }
        var target = _capturedElement ?? hit;
        if (target is not null)
        {
            var handled = DispatchPointer(
                target,
                UIPointerEventKind.Move,
                pointerEvent.PointerId,
                pointerEvent.Position,
                pointerEvent.Delta,
                Vector2.Zero,
                InputPointerButton.Unknown,
                0,
                pointerEvent.DeviceKind,
                pointerEvent.Modifiers,
                pointerEvent.PressedButtons);
            if (handled)
                _invalidate();
        }
    }

    /// <summary>Focuses and presses the hovered element.</summary>
    public void Press()
    {
        Press(new PointerButtonEvent(
            0, _pointerPosition, InputPointerButton.Primary, true, 1,
            PointerDeviceKind.Mouse, InputModifiers.None, PointerButtons.Primary));
    }

    /// <summary>Routes one device-neutral pointer press.</summary>
    /// <param name="pointerEvent">Pressed-button event.</param>
    public void Press(PointerButtonEvent pointerEvent)
    {
        _root.InvalidateTimeUpdateActivity();
        EnsurePointerCaptureValid();
        _pointerPosition = pointerEvent.Position;
        var scope = GetInputScope();
        var target = _capturedElement ?? HitTest(scope, pointerEvent.Position, null);
        if (_capturedElement is null && FindTopmostPopup(scope) is { StaysOpen: false } popup &&
            !IsDescendantOrSelf(target, popup) && !IsDescendantOrSelf(target, popup.Owner))
        {
            popup.Close();
            target = HitTest(scope, pointerEvent.Position, null);
            _invalidate();
        }
        _dragCandidate = pointerEvent.Button == InputPointerButton.Primary && target is not null
            ? FindDragSource(target)
            : null;
        _dragStartPosition = pointerEvent.Position;
        if (target is null || DispatchPointer(
                target,
                UIPointerEventKind.Press,
                pointerEvent.PointerId,
                pointerEvent.Position,
                Vector2.Zero,
                Vector2.Zero,
                pointerEvent.Button,
                pointerEvent.ClickCount,
                pointerEvent.DeviceKind,
                pointerEvent.Modifiers,
                pointerEvent.PressedButtons))
            return;
        _pressedElement?.SetPressed(false);
        _pressedElement = target;
        SetFocus(_pressedElement);
        _pressedElement?.SetPressed(true);
        _invalidate();
    }

    /// <summary>Releases the hovered element and optionally invokes its click.</summary>
    /// <param name="invokeClick">Whether to invoke the click event.</param>
    public void Release(bool invokeClick)
    {
        Release(new PointerButtonEvent(
            0, _pointerPosition, InputPointerButton.Primary, false, 1,
            PointerDeviceKind.Mouse, InputModifiers.None, PointerButtons.None), invokeClick);
    }

    /// <summary>Routes one device-neutral pointer release.</summary>
    /// <param name="pointerEvent">Released-button event.</param>
    /// <param name="invokeClick">Whether compatible click behavior is allowed.</param>
    public void Release(PointerButtonEvent pointerEvent, bool invokeClick)
    {
        _root.InvalidateTimeUpdateActivity();
        EnsurePointerCaptureValid();
        _pointerPosition = pointerEvent.Position;
        _dragCandidate = null;
        if (_dragSource is not null)
        {
            CompleteDrag(pointerEvent.Position);
            ClearPressedElement();
            return;
        }
        var releaseTarget = _capturedElement ?? HitTest(GetInputScope(), pointerEvent.Position, null);
        var handled = releaseTarget is not null && DispatchPointer(
                releaseTarget,
                UIPointerEventKind.Release,
                pointerEvent.PointerId,
                pointerEvent.Position,
                Vector2.Zero,
                Vector2.Zero,
                pointerEvent.Button,
                pointerEvent.ClickCount,
                pointerEvent.DeviceKind,
                pointerEvent.Modifiers,
                pointerEvent.PressedButtons);
        if (_pressedElement is null)
            return;

        var pressedElement = _pressedElement;
        _pressedElement = null;
        pressedElement.SetPressed(false);
        if (!handled && invokeClick && ReferenceEquals(pressedElement, releaseTarget))
        {
            pressedElement.InvokeClick();
            RestoreFocusFromClosedPopup(pressedElement);
        }
        _invalidate();
    }

    /// <summary>Begins a drag operation without waiting for automatic threshold detection.</summary>
    /// <param name="source">Element that owns the drag.</param>
    /// <param name="data">Typed payload.</param>
    /// <param name="allowedEffects">Operations permitted by the source.</param>
    public void StartDrag(UIElement source, UIDragData data, UIDragEffect allowedEffects)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(data);
        if (!IsRouteElementEligible(source))
            throw new InvalidOperationException("A drag source must be enabled in the active input scope.");
        CancelDrag();
        _dragCandidate = null;
        _dragSource = source;
        _dragData = data;
        _allowedDragEffects = allowedEffects;
        _dragEffect = UIDragEffect.None;
        CapturePointer(source);
        DragStateChanged?.Invoke();
        _invalidate();
    }

    /// <summary>Cancels the active drag and sends leave and cancel notifications.</summary>
    public void CancelDrag()
    {
        _root.InvalidateTimeUpdateActivity();
        _dragCandidate = null;
        if (_dragSource is null || _dragData is null)
            return;
        var source = _dragSource;
        if (_dropTarget is { } target)
            DispatchDrag(target, UIDragEventKind.Leave, _pointerPosition);
        DispatchDrag(source, UIDragEventKind.Cancel, _pointerPosition);
        ResetDragState();
        DragStateChanged?.Invoke();
        _invalidate();
    }

    /// <summary>Clears compatible pressed state without generating a click.</summary>
    private void ClearPressedElement()
    {
        if (_pressedElement is not { } pressed)
            return;
        _pressedElement = null;
        pressed.SetPressed(false);
        _invalidate();
    }

    /// <summary>Updates target transitions and effect negotiation for an active drag.</summary>
    /// <param name="hit">Raw hit-tested element.</param>
    /// <param name="position">Host pointer position.</param>
    private void UpdateDrag(UIElement? hit, Vector2 position)
    {
        var target = FindDropTarget(hit);
        if (!ReferenceEquals(target, _dropTarget))
        {
            if (_dropTarget is { } previous)
                DispatchDrag(previous, UIDragEventKind.Leave, position);
            _dropTarget = target;
            _dragEffect = UIDragEffect.None;
            if (target is not null)
                _dragEffect = DispatchDrag(target, UIDragEventKind.Enter, position);
        }
        if (_dropTarget is { } current)
            _dragEffect = DispatchDrag(current, UIDragEventKind.Over, position);
        DragStateChanged?.Invoke();
        _invalidate();
    }

    /// <summary>Drops on the current target when it accepted an allowed effect.</summary>
    /// <param name="position">Host pointer position.</param>
    private void CompleteDrag(Vector2 position)
    {
        if (_dropTarget is { } target && _dragEffect != UIDragEffect.None)
            DispatchDrag(target, UIDragEventKind.Drop, position);
        else if (_dragSource is { } source)
            DispatchDrag(source, UIDragEventKind.Cancel, position);
        ResetDragState();
        DragStateChanged?.Invoke();
        _invalidate();
    }

    /// <summary>Resets drag state and releases capture without recursively cancelling.</summary>
    private void ResetDragState()
    {
        _dragSource = null;
        _dropTarget = null;
        _dragData = null;
        _allowedDragEffects = UIDragEffect.None;
        _dragEffect = UIDragEffect.None;
        _completingDrag = true;
        ReleasePointerCapture();
        _completingDrag = false;
    }

    /// <summary>Finds the nearest ancestor exposing automatic drag data.</summary>
    /// <param name="element">Original pressed element.</param>
    /// <returns>Nearest drag source, or null.</returns>
    private static UIElement? FindDragSource(UIElement element)
    {
        UIElement? current = element;
        while (current is not null)
        {
            if (current.DragData is not null && current.AllowedDragEffects != UIDragEffect.None)
                return current;
            current = current.Parent as UIElement;
        }
        return null;
    }

    /// <summary>Finds the nearest enabled ancestor that accepts drops.</summary>
    /// <param name="element">Raw hit-tested element.</param>
    /// <returns>Drop target, or null.</returns>
    private static UIElement? FindDropTarget(UIElement? element)
    {
        var current = element;
        while (current is not null)
        {
            if (current.AllowDrop)
                return current;
            current = current.Parent as UIElement;
        }
        return null;
    }

    /// <summary>Routes one drag event and returns its source-constrained accepted effect.</summary>
    /// <param name="target">Original drag target.</param>
    /// <param name="kind">Semantic drag event kind.</param>
    /// <param name="position">Host pointer position.</param>
    /// <returns>Accepted effect intersected with the source's allowed effects.</returns>
    private UIDragEffect DispatchDrag(UIElement target, UIDragEventKind kind, Vector2 position)
    {
        if (_dragSource is null || _dragData is null)
            return UIDragEffect.None;
        if (_routeDepth == _routeStates.Count)
            _routeStates.Add(new RouteDispatchState());
        var state = _routeStates[_routeDepth++];
        try
        {
            var routeCount = BuildRoute(target, state);
            var dragEvent = state.DragEventArgs;
            dragEvent.Reset(kind, _dragSource, target, _dragData,
                _allowedDragEffects, _dragEffect, position);
            for (var index = routeCount - 1; index >= 0; index--)
            {
                var element = state.Route[index];
                if (!IsRouteElementEligible(element))
                    continue;
                dragEvent.SetCurrentTarget(element, UIRoutePhase.Preview);
                element.InvokePreviewDrag(dragEvent);
                if (dragEvent.Handled)
                    return dragEvent.Effect & _allowedDragEffects;
            }
            if (IsRouteElementEligible(target))
            {
                dragEvent.SetCurrentTarget(target, UIRoutePhase.Target);
                target.InvokeDrag(dragEvent);
            }
            if (!dragEvent.Handled)
            {
                for (var index = 1; index < routeCount; index++)
                {
                    var element = state.Route[index];
                    if (!IsRouteElementEligible(element))
                        continue;
                    dragEvent.SetCurrentTarget(element, UIRoutePhase.Bubble);
                    element.InvokeDrag(dragEvent);
                    if (dragEvent.Handled)
                        break;
                }
            }
            return dragEvent.Effect & _allowedDragEffects;
        }
        finally
        {
            _routeDepth--;
        }
    }

    /// <summary>Dispatches a double click to the hovered element.</summary>
    public void DoubleClick()
    {
        DoubleClick(new PointerButtonEvent(
            0, _pointerPosition, InputPointerButton.Primary, true, 2,
            PointerDeviceKind.Mouse, InputModifiers.None, PointerButtons.Primary));
    }

    /// <summary>Routes one device-neutral pointer double click.</summary>
    /// <param name="pointerEvent">Double-click event.</param>
    public void DoubleClick(PointerButtonEvent pointerEvent)
    {
        EnsurePointerCaptureValid();
        _pointerPosition = pointerEvent.Position;
        var target = _capturedElement ?? HitTest(GetInputScope(), pointerEvent.Position, null);
        if (target is not null && !DispatchPointer(
                target,
                UIPointerEventKind.DoubleClick,
                pointerEvent.PointerId,
                pointerEvent.Position,
                Vector2.Zero,
                Vector2.Zero,
                pointerEvent.Button,
                pointerEvent.ClickCount,
                pointerEvent.DeviceKind,
                pointerEvent.Modifiers,
                pointerEvent.PressedButtons))
            target.InvokeDoubleClick();
        _invalidate();
    }

    /// <summary>Dispatches mouse-wheel input to the hovered element.</summary>
    /// <param name="offset">Wheel offset.</param>
    public void Scroll(float offset)
    {
        Scroll(new PointerWheelEvent(
            0, _pointerPosition, new Vector2(0f, offset), InputModifiers.None));
    }

    /// <summary>Routes device-neutral pointer-wheel input.</summary>
    /// <param name="pointerEvent">Wheel event to route.</param>
    public void Scroll(PointerWheelEvent pointerEvent)
    {
        _root.InvalidateTimeUpdateActivity();
        EnsurePointerCaptureValid();
        _pointerPosition = pointerEvent.Position;
        var target = _capturedElement ?? HitTest(GetInputScope(), pointerEvent.Position, null);
        if (target is not null && !DispatchPointer(
                target,
                UIPointerEventKind.Wheel,
                pointerEvent.PointerId,
                pointerEvent.Position,
                Vector2.Zero,
                pointerEvent.Delta,
                InputPointerButton.Unknown,
                0,
                PointerDeviceKind.Mouse,
                pointerEvent.Modifiers,
                PointerButtons.None))
            target.InvokeScroll(pointerEvent.Delta.Y);
        _invalidate();
    }

    /// <summary>Updates compatible hover state after hit testing.</summary>
    /// <param name="hit">Current topmost hit element.</param>
    private void UpdateHoveredElement(UIElement? hit)
    {
        if (ReferenceEquals(hit, HoveredElement))
            return;
        HoveredElement?.SetHover(false);
        HoveredElement = hit;
        HoveredElement?.SetHover(true);
        DiagnosticStateChanged?.Invoke();
        _invalidate();
    }

    /// <summary>Dispatches one pointer event through a stable preview, target, and bubble route.</summary>
    /// <param name="target">Original route target.</param>
    /// <param name="kind">Pointer event kind.</param>
    /// <param name="pointerId">Device-local pointer identifier.</param>
    /// <param name="position">Root-relative pointer position.</param>
    /// <param name="delta">Pointer movement delta.</param>
    /// <param name="wheelDelta">Wheel movement delta.</param>
    /// <param name="button">Changed pointer button.</param>
    /// <param name="clickCount">Native click count.</param>
    /// <param name="deviceKind">Pointer device kind.</param>
    /// <param name="modifiers">Active keyboard modifiers.</param>
    /// <param name="pressedButtons">Buttons pressed after the transition.</param>
    /// <returns>True when a routed handler marks the event handled.</returns>
    private bool DispatchPointer(
        UIElement target,
        UIPointerEventKind kind,
        int pointerId,
        Vector2 position,
        Vector2 delta,
        Vector2 wheelDelta,
        InputPointerButton button,
        int clickCount,
        PointerDeviceKind deviceKind,
        InputModifiers modifiers,
        PointerButtons pressedButtons)
    {
        if (_routeDepth == _routeStates.Count)
            _routeStates.Add(new RouteDispatchState());
        var state = _routeStates[_routeDepth++];
        try
        {
            var routeCount = BuildRoute(target, state);
            var pointerEvent = state.EventArgs;
            pointerEvent.Reset(
                kind, target, pointerId, position, delta, wheelDelta, button,
                clickCount, deviceKind, modifiers, pressedButtons,
                _capturePointerAction, _releasePointerCaptureAction);

            for (var index = routeCount - 1; index >= 0; index--)
            {
                var element = state.Route[index];
                if (!IsRouteElementEligible(element))
                    continue;
                pointerEvent.SetCurrentTarget(element, UIRoutePhase.Preview);
                element.InvokePreviewPointer(pointerEvent);
                if (pointerEvent.Handled)
                    return true;
            }

            if (IsRouteElementEligible(target))
            {
                pointerEvent.SetCurrentTarget(target, UIRoutePhase.Target);
                target.InvokePointer(pointerEvent);
                if (pointerEvent.Handled)
                    return true;
            }

            for (var index = 1; index < routeCount; index++)
            {
                var element = state.Route[index];
                if (!IsRouteElementEligible(element))
                    continue;
                pointerEvent.SetCurrentTarget(element, UIRoutePhase.Bubble);
                element.InvokePointer(pointerEvent);
                if (pointerEvent.Handled)
                    return true;
            }

            return false;
        }
        finally
        {
            _routeDepth--;
        }
    }

    /// <summary>Snapshots the target-to-root route before invoking user handlers.</summary>
    /// <param name="target">Original route target.</param>
    /// <param name="state">Reusable dispatch storage.</param>
    /// <returns>Number of elements in the route.</returns>
    private int BuildRoute(UIElement target, RouteDispatchState state)
    {
        var count = 0;
        var scope = GetInputScope();
        UIElement? current = target;
        while (current is not null)
        {
            state.EnsureCapacity(count + 1);
            state.Route[count++] = current;
            if (ReferenceEquals(current, scope))
                break;
            current = current.Parent as UIElement;
        }
        return count;
    }

    /// <summary>Checks whether a snapshotted route element can still receive this dispatch.</summary>
    /// <param name="element">Route element to inspect.</param>
    /// <returns>True when the element remains visible and attached to this router's root.</returns>
    private bool IsRouteElementEligible(UIElement element) =>
        element.IsVisible && IsEffectivelyEnabled(element) && IsInInputScope(element);

    /// <summary>Dispatches a compatible key press to the focused element.</summary>
    /// <param name="keyCode">Engine key code.</param>
    public void KeyDown(int keyCode)
    {
        RouteKey(new KeyInputEvent((InputKey)keyCode, true, false, InputModifiers.None));
    }

    /// <summary>Dispatches a key release to the focused element.</summary>
    /// <param name="keyCode">Engine key code.</param>
    public void KeyUp(int keyCode)
    {
        RouteKey(new KeyInputEvent((InputKey)keyCode, false, false, InputModifiers.None));
    }

    /// <summary>Routes one device-neutral keyboard transition.</summary>
    /// <param name="keyEvent">Keyboard transition to route.</param>
    public void RouteKey(KeyInputEvent keyEvent)
    {
        _root.InvalidateTimeUpdateActivity();
        EnsureFocusValid();
        if (keyEvent.IsPressed && keyEvent.Key == InputKey.Escape &&
            FindTopmostPopup(GetInputScope()) is { StaysOpen: false } popup)
        {
            var owner = popup.Owner;
            popup.Close();
            if (owner is not null && IsRouteElementEligible(owner))
                SetFocus(owner);
            _invalidate();
            return;
        }
        if (FocusedElement is not { } target)
        {
            if (keyEvent.IsPressed && ExecuteGesture(GetInputScope(), keyEvent))
            {
                _invalidate();
                return;
            }
            if (keyEvent.IsPressed && keyEvent.Key == InputKey.Tab)
                MoveFocus((keyEvent.Modifiers & InputModifiers.Shift) == 0);
            return;
        }

        if (keyEvent.IsPressed && ExecuteGesture(target, keyEvent))
        {
            _invalidate();
            return;
        }

        var handled = DispatchKey(target, keyEvent);
        if (!handled && keyEvent.IsPressed && keyEvent.Key == InputKey.Tab)
            handled = MoveFocus((keyEvent.Modifiers & InputModifiers.Shift) == 0);
        if (!handled && IsRouteElementEligible(target))
        {
            if (keyEvent.IsPressed)
                target.InvokeKeyDown((int)keyEvent.Key);
            else
                target.InvokeKeyUp((int)keyEvent.Key);
        }
        RestoreFocusFromClosedPopup(target);
        _invalidate();
    }

    /// <summary>Finds and executes the nearest enabled key binding in the active scope.</summary>
    /// <param name="target">Focused gesture target.</param>
    /// <param name="keyEvent">Keyboard transition.</param>
    /// <returns>True when a matching command executes.</returns>
    private bool ExecuteGesture(UIElement target, KeyInputEvent keyEvent)
    {
        if (_routeDepth == _routeStates.Count)
            _routeStates.Add(new RouteDispatchState());
        var state = _routeStates[_routeDepth++];
        try
        {
            var routeCount = BuildRoute(target, state);
            for (var routeIndex = 0; routeIndex < routeCount; routeIndex++)
            {
                var element = state.Route[routeIndex];
                if (!IsRouteElementEligible(element))
                    continue;
                var bindings = element.KeyBindings;
                for (var bindingIndex = 0; bindingIndex < bindings.Count; bindingIndex++)
                {
                    var binding = bindings[bindingIndex];
                    if (binding.Gesture.Matches(keyEvent, binding.AllowsRepeat) &&
                        ExecuteCommand(binding.Command, binding.Parameter, target))
                        return true;
                }
            }
            return false;
        }
        finally
        {
            _routeDepth--;
        }
    }

    /// <summary>Dispatches a compatible text-input character to the focused element.</summary>
    /// <param name="character">Produced text character.</param>
    public void TextInput(char character)
    {
        RouteText(character.ToString());
    }

    /// <summary>Routes committed Unicode text to the focused element.</summary>
    /// <param name="text">Committed text.</param>
    public void RouteText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        _root.InvalidateTimeUpdateActivity();
        EnsureFocusValid();
        if (FocusedElement is not { } target || text.Length == 0)
            return;
        if (!DispatchText(target, text) && IsRouteElementEligible(target))
        {
            for (var index = 0; index < text.Length; index++)
                target.InvokeTextInput(text[index]);
        }
        _invalidate();
    }

    /// <summary>Routes one device-neutral input-method composition transition.</summary>
    /// <param name="composition">Composition transition to route.</param>
    public void RouteTextComposition(TextCompositionEvent composition)
    {
        _root.InvalidateTimeUpdateActivity();
        EnsureFocusValid();
        if (FocusedElement is not { } target)
            return;
        DispatchTextComposition(target, composition);
        _invalidate();
    }

    /// <summary>Moves keyboard focus to an explicit element or clears it.</summary>
    /// <param name="element">Attached visible element to focus, or null to clear focus.</param>
    public void Focus(UIElement? element)
    {
        if (element is not null && (!element.IsVisible || !IsEffectivelyEnabled(element) || !IsInInputScope(element)))
            throw new InvalidOperationException("Keyboard focus requires an enabled visible element in the active input scope.");
        SetFocus(element);
        _invalidate();
    }

    /// <summary>Moves focus through visible tab stops in deterministic tree order.</summary>
    /// <param name="forward">True for the next tab stop; false for the previous one.</param>
    /// <returns>True when a tab stop receives focus.</returns>
    public bool MoveFocus(bool forward)
    {
        EnsureFocusValid();
        _focusTraversal.Clear();
        CollectTabStops(GetInputScope());
        if (_focusTraversal.Count == 0)
            return false;
        SortTabStops();

        var currentIndex = -1;
        for (var index = 0; index < _focusTraversal.Count; index++)
        {
            if (ReferenceEquals(_focusTraversal[index], FocusedElement))
            {
                currentIndex = index;
                break;
            }
        }
        var nextIndex = forward
            ? (currentIndex + 1) % _focusTraversal.Count
            : (currentIndex <= 0 ? _focusTraversal.Count : currentIndex) - 1;
        SetFocus(_focusTraversal[nextIndex]);
        _invalidate();
        return true;
    }

    /// <summary>Routes one device-neutral controller navigation transition.</summary>
    /// <param name="navigationEvent">Controller navigation transition.</param>
    /// <returns>True when UI consumed the action.</returns>
    public bool RouteNavigation(NavigationInputEvent navigationEvent)
    {
        _root.InvalidateTimeUpdateActivity();
        EnsureFocusValid();
        if (!navigationEvent.IsPressed)
            return false;
        if (navigationEvent.Action == UINavigationAction.Cancel)
        {
            if (FindTopmostPopup(GetInputScope()) is not { StaysOpen: false } popup)
                return false;
            var owner = popup.Owner;
            popup.Close();
            if (owner is not null && IsRouteElementEligible(owner))
                SetFocus(owner);
            _invalidate();
            return true;
        }
        if (navigationEvent.Action == UINavigationAction.Submit)
        {
            if (FocusedElement is not { } target || !IsRouteElementEligible(target))
                return false;
            target.InvokeClick();
            _invalidate();
            return true;
        }
        var direction = navigationEvent.Action switch
        {
            UINavigationAction.Up => new Vector2(0f, -1f),
            UINavigationAction.Down => new Vector2(0f, 1f),
            UINavigationAction.Left => new Vector2(-1f, 0f),
            UINavigationAction.Right => new Vector2(1f, 0f),
            _ => Vector2.Zero
        };
        if (direction == Vector2.Zero)
            return false;
        var key = navigationEvent.Action switch
        {
            UINavigationAction.Up => InputKey.Up,
            UINavigationAction.Down => InputKey.Down,
            UINavigationAction.Left => InputKey.Left,
            _ => InputKey.Right
        };
        if (FocusedElement is { } focused && DispatchKey(focused,
                new KeyInputEvent(key, true, navigationEvent.IsRepeat, InputModifiers.None)))
        {
            _invalidate();
            return true;
        }
        var moved = MoveFocus(direction);
        return moved;
    }

    /// <summary>Moves focus to the nearest visible tab stop in a spatial direction.</summary>
    /// <param name="direction">Non-zero logical direction.</param>
    /// <returns>True when a candidate receives focus.</returns>
    public bool MoveFocus(Vector2 direction)
    {
        EnsureFocusValid();
        if (!float.IsFinite(direction.X) || !float.IsFinite(direction.Y) ||
            direction.LengthSquared() <= float.Epsilon)
            throw new ArgumentOutOfRangeException(nameof(direction));
        _focusTraversal.Clear();
        CollectTabStops(GetInputScope());
        if (_focusTraversal.Count == 0)
            return false;
        SortTabStops();
        if (FocusedElement is not { } current || !_focusTraversal.Contains(current))
        {
            SetFocus(_focusTraversal[0]);
            _invalidate();
            return true;
        }
        direction = Vector2.Normalize(direction);
        var origin = new Vector2(
            (current.Left + current.Right) * 0.5f,
            (current.Top + current.Bottom) * 0.5f);
        UIElement? best = null;
        var bestScore = float.PositiveInfinity;
        for (var index = 0; index < _focusTraversal.Count; index++)
        {
            var candidate = _focusTraversal[index];
            if (ReferenceEquals(candidate, current))
                continue;
            var offset = new Vector2(
                (candidate.Left + candidate.Right) * 0.5f,
                (candidate.Top + candidate.Bottom) * 0.5f) - origin;
            var forward = Vector2.Dot(offset, direction);
            if (forward <= 0f)
                continue;
            var perpendicular = offset - direction * forward;
            var score = forward * forward + perpendicular.LengthSquared() * 4f;
            if (score >= bestScore)
                continue;
            bestScore = score;
            best = candidate;
        }
        if (best is null)
            return false;
        SetFocus(best);
        _invalidate();
        return true;
    }

    /// <summary>Dispatches a key event through a stable preview, target, and bubble route.</summary>
    /// <param name="target">Focused route target.</param>
    /// <param name="keyEvent">Keyboard transition.</param>
    /// <returns>True when a routed handler marks the event handled.</returns>
    private bool DispatchKey(UIElement target, KeyInputEvent keyEvent)
    {
        if (_routeDepth == _routeStates.Count)
            _routeStates.Add(new RouteDispatchState());
        var state = _routeStates[_routeDepth++];
        try
        {
            var routeCount = BuildRoute(target, state);
            var routedEvent = state.KeyEventArgs;
            routedEvent.Reset(
                keyEvent.IsPressed ? UIKeyEventKind.KeyDown : UIKeyEventKind.KeyUp,
                target, keyEvent.Key, keyEvent.IsRepeat, keyEvent.Modifiers, _focusAction);
            for (var index = routeCount - 1; index >= 0; index--)
            {
                var element = state.Route[index];
                if (!IsRouteElementEligible(element))
                    continue;
                routedEvent.SetCurrentTarget(element, UIRoutePhase.Preview);
                element.InvokePreviewKey(routedEvent);
                if (routedEvent.Handled)
                    return true;
            }
            if (IsRouteElementEligible(target))
            {
                routedEvent.SetCurrentTarget(target, UIRoutePhase.Target);
                target.InvokeKey(routedEvent);
                if (routedEvent.Handled)
                    return true;
            }
            for (var index = 1; index < routeCount; index++)
            {
                var element = state.Route[index];
                if (!IsRouteElementEligible(element))
                    continue;
                routedEvent.SetCurrentTarget(element, UIRoutePhase.Bubble);
                element.InvokeKey(routedEvent);
                if (routedEvent.Handled)
                    return true;
            }
            return false;
        }
        finally
        {
            _routeDepth--;
        }
    }

    /// <summary>Dispatches committed text through a stable preview, target, and bubble route.</summary>
    /// <param name="target">Focused route target.</param>
    /// <param name="text">Committed Unicode text.</param>
    /// <returns>True when a routed handler marks the event handled.</returns>
    private bool DispatchText(UIElement target, string text)
    {
        if (_routeDepth == _routeStates.Count)
            _routeStates.Add(new RouteDispatchState());
        var state = _routeStates[_routeDepth++];
        try
        {
            var routeCount = BuildRoute(target, state);
            var routedEvent = state.TextEventArgs;
            routedEvent.Reset(target, text, _focusAction);
            for (var index = routeCount - 1; index >= 0; index--)
            {
                var element = state.Route[index];
                if (!IsRouteElementEligible(element))
                    continue;
                routedEvent.SetCurrentTarget(element, UIRoutePhase.Preview);
                element.InvokePreviewText(routedEvent);
                if (routedEvent.Handled)
                    return true;
            }
            if (IsRouteElementEligible(target))
            {
                routedEvent.SetCurrentTarget(target, UIRoutePhase.Target);
                target.InvokeText(routedEvent);
                if (routedEvent.Handled)
                    return true;
            }
            for (var index = 1; index < routeCount; index++)
            {
                var element = state.Route[index];
                if (!IsRouteElementEligible(element))
                    continue;
                routedEvent.SetCurrentTarget(element, UIRoutePhase.Bubble);
                element.InvokeText(routedEvent);
                if (routedEvent.Handled)
                    return true;
            }
            return false;
        }
        finally
        {
            _routeDepth--;
        }
    }

    /// <summary>Dispatches composition through a stable preview, target, and bubble route.</summary>
    /// <param name="target">Focused composition target.</param>
    /// <param name="composition">Device-neutral composition transition.</param>
    /// <returns>True when a routed handler marks the event handled.</returns>
    private bool DispatchTextComposition(UIElement target, TextCompositionEvent composition)
    {
        if (_routeDepth == _routeStates.Count)
            _routeStates.Add(new RouteDispatchState());
        var state = _routeStates[_routeDepth++];
        try
        {
            var routeCount = BuildRoute(target, state);
            var routedEvent = state.CompositionEventArgs;
            routedEvent.Reset(target, composition);
            for (var index = routeCount - 1; index >= 0; index--)
            {
                var element = state.Route[index];
                if (!IsRouteElementEligible(element))
                    continue;
                routedEvent.SetCurrentTarget(element, UIRoutePhase.Preview);
                element.InvokePreviewTextComposition(routedEvent);
                if (routedEvent.Handled)
                    return true;
            }
            if (IsRouteElementEligible(target))
            {
                routedEvent.SetCurrentTarget(target, UIRoutePhase.Target);
                target.InvokeTextComposition(routedEvent);
                if (routedEvent.Handled)
                    return true;
            }
            for (var index = 1; index < routeCount; index++)
            {
                var element = state.Route[index];
                if (!IsRouteElementEligible(element))
                    continue;
                routedEvent.SetCurrentTarget(element, UIRoutePhase.Bubble);
                element.InvokeTextComposition(routedEvent);
                if (routedEvent.Handled)
                    return true;
            }
            return false;
        }
        finally
        {
            _routeDepth--;
        }
    }

    /// <summary>Collects visible sequential-focus candidates without iterator allocation.</summary>
    /// <param name="element">Subtree root.</param>
    private void CollectTabStops(UIElement element)
    {
        if (!element.IsVisible || !IsEffectivelyEnabled(element))
            return;
        if (element.IsTabStop)
            _focusTraversal.Add(element);
        var children = element.Children;
        for (var index = 0; index < children.Count; index++)
        {
            if (children[index] is UIElement child)
                CollectTabStops(child);
        }
    }

    /// <summary>Stably orders collected tab stops by tab index without allocating comparer delegates.</summary>
    private void SortTabStops()
    {
        for (var index = 1; index < _focusTraversal.Count; index++)
        {
            var candidate = _focusTraversal[index];
            var insertion = index;
            while (insertion > 0 && _focusTraversal[insertion - 1].TabIndex > candidate.TabIndex)
            {
                _focusTraversal[insertion] = _focusTraversal[insertion - 1];
                insertion--;
            }
            _focusTraversal[insertion] = candidate;
        }
    }

    /// <summary>Captures subsequent pointer input to one element in this router's tree.</summary>
    /// <param name="element">Element that should receive pointer input.</param>
    public void CapturePointer(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (!IsRouteElementEligible(element))
            throw new InvalidOperationException("Pointer capture requires an enabled element in the active input scope.");
        if (ReferenceEquals(_capturedElement, element))
            return;
        ReleasePointerCapture();
        _capturedElement = element;
        DiagnosticStateChanged?.Invoke();
        _invalidate();
    }

    /// <summary>Releases explicit pointer capture, if any.</summary>
    public void ReleasePointerCapture()
    {
        _root.InvalidateTimeUpdateActivity();
        if (_capturedElement is not { } captured)
            return;
        _capturedElement = null;
        DiagnosticStateChanged?.Invoke();
        captured.InvokePointerCaptureLost();
        if (!_completingDrag && ReferenceEquals(captured, _dragSource))
            CancelDrag();
        _invalidate();
    }

    /// <summary>Releases capture when its element is no longer eligible.</summary>
    private void EnsurePointerCaptureValid()
    {
        if (_capturedElement is not { } captured)
            return;
        if (!IsRouteElementEligible(captured))
            ReleasePointerCapture();
    }

    /// <summary>Clears keyboard focus when its element is hidden or detached.</summary>
    private void EnsureFocusValid()
    {
        if (FocusedElement is not { } focused)
            return;
        if (!IsRouteElementEligible(focused))
            SetFocus(null);
    }

    /// <summary>Executes the first enabled command binding from target toward the active scope root.</summary>
    /// <param name="command">Command to route.</param>
    /// <param name="parameter">Optional command parameter.</param>
    /// <param name="target">Optional target; focused element is used when omitted.</param>
    /// <returns>True when a binding executes the command.</returns>
    public bool ExecuteCommand(UICommand command, object? parameter = null, UIElement? target = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureFocusValid();
        target ??= FocusedElement ?? GetInputScope();
        if (!IsRouteElementEligible(target))
            return false;
        if (_routeDepth == _routeStates.Count)
            _routeStates.Add(new RouteDispatchState());
        var state = _routeStates[_routeDepth++];
        try
        {
            var routeCount = BuildRoute(target, state);
            var commandEvent = state.CommandEventArgs;
            commandEvent.Command = command;
            commandEvent.Parameter = parameter;
            commandEvent.Source = target;
            commandEvent.Clipboard = _clipboard;
            for (var routeIndex = 0; routeIndex < routeCount; routeIndex++)
            {
                var element = state.Route[routeIndex];
                if (!IsRouteElementEligible(element))
                    continue;
                var bindings = element.CommandBindings;
                for (var bindingIndex = 0; bindingIndex < bindings.Count; bindingIndex++)
                {
                    var binding = bindings[bindingIndex];
                    if (!ReferenceEquals(binding.Command, command))
                        continue;
                    commandEvent.CurrentTarget = element;
                    commandEvent.CanExecute = true;
                    commandEvent.Handled = false;
                    binding.CanExecute?.Invoke(commandEvent);
                    if (commandEvent.Handled && !commandEvent.CanExecute)
                        return false;
                    if (!commandEvent.CanExecute)
                        continue;
                    binding.Execute(commandEvent);
                    _invalidate();
                    return true;
                }
            }
            return false;
        }
        finally
        {
            _routeDepth--;
        }
    }

    /// <summary>Gets the topmost visible modal subtree or the host root when no modal is active.</summary>
    /// <returns>Active input scope.</returns>
    private UIElement GetInputScope()
    {
        var version = _root.InputTreeVersion;
        if (_cachedInputScope is not null && _cachedInputScopeVersion == version)
            return _cachedInputScope;
        _cachedInputScope = FindTopmostModal(_root) ?? _root;
        _cachedInputScopeVersion = version;
        return _cachedInputScope;
    }

    /// <summary>Finds the last visible modal in paint order.</summary>
    /// <param name="element">Subtree root.</param>
    /// <returns>Topmost modal, or null.</returns>
    private static Modal? FindTopmostModal(UIElement element)
    {
        var children = element.Children;
        for (var index = children.Count - 1; index >= 0; index--)
        {
            if (children[index] is not UIElement child || !child.IsVisible)
                continue;
            if (FindTopmostModal(child) is { } nested)
                return nested;
            if (child is Modal modal)
                return modal;
        }
        return element is Modal self && self.IsVisible ? self : null;
    }

    /// <summary>Finds the topmost open popup in one active input scope.</summary>
    /// <param name="element">Subtree root.</param>
    /// <returns>Topmost popup, or null.</returns>
    private static Popup? FindTopmostPopup(UIElement element)
    {
        var children = element.Children;
        for (var index = children.Count - 1; index >= 0; index--)
        {
            if (children[index] is not UIElement child || !child.IsVisible)
                continue;
            if (FindTopmostPopup(child) is { } nested)
                return nested;
            if (child is Popup popup)
                return popup;
        }
        return element is Popup self && self.IsVisible ? self : null;
    }

    /// <summary>Checks whether an element is a descendant of an optional ancestor.</summary>
    /// <param name="element">Potential descendant.</param>
    /// <param name="ancestor">Potential ancestor.</param>
    /// <returns>True when both are non-null and the ancestor is reachable.</returns>
    private static bool IsDescendantOrSelf(UIElement? element, UIElement? ancestor)
    {
        if (element is null || ancestor is null)
            return false;
        Node? current = element;
        while (current is not null)
        {
            if (ReferenceEquals(current, ancestor))
                return true;
            current = current.Parent;
        }
        return false;
    }

    /// <summary>Restores focus to the first eligible owner after an action closes its popup chain.</summary>
    /// <param name="source">Element that handled the closing activation.</param>
    private void RestoreFocusFromClosedPopup(UIElement source)
    {
        Node? current = source;
        while (current is not null)
        {
            if (current is Popup { IsOpen: false, Owner: { } owner } && IsRouteElementEligible(owner))
            {
                SetFocus(owner);
                return;
            }
            current = current.Parent;
        }
    }

    /// <summary>Checks whether an element belongs to the currently active modal or host scope.</summary>
    /// <param name="element">Element to inspect.</param>
    /// <returns>True when input may route to the element.</returns>
    private bool IsInInputScope(UIElement element)
    {
        if (!IsInRootTree(element))
            return false;
        var scope = GetInputScope();
        Node? current = element;
        while (current is not null)
        {
            if (ReferenceEquals(current, scope))
                return true;
            current = current.Parent;
        }
        return false;
    }

    /// <summary>Checks local and inherited enabled state.</summary>
    /// <param name="element">Element to inspect.</param>
    /// <returns>True when the element and all UI ancestors are enabled.</returns>
    private static bool IsEffectivelyEnabled(UIElement element)
    {
        UIElement? current = element;
        while (current is not null)
        {
            if (!current.IsEnabled)
                return false;
            current = current.Parent as UIElement;
        }
        return true;
    }

    /// <summary>Checks whether an element belongs to the current routed tree.</summary>
    /// <param name="element">Element to inspect.</param>
    /// <returns>True when the root is reachable through parent links.</returns>
    private bool IsInRootTree(UIElement element)
    {
        Node? current = element;
        while (current is not null)
        {
            if (ReferenceEquals(current, _root))
                return true;
            current = current.Parent;
        }
        return false;
    }

    /// <summary>Changes keyboard focus.</summary>
    /// <param name="element">Element to focus, or null to clear focus.</param>
    private void SetFocus(UIElement? element)
    {
        if (ReferenceEquals(element, FocusedElement))
            return;

        FocusedElement?.SetFocus(false);
        FocusedElement = element;
        FocusedElement?.SetFocus(true);
        DiagnosticStateChanged?.Invoke();
    }

    /// <summary>Finds the topmost visible element containing a point.</summary>
    /// <param name="element">Subtree root.</param>
    /// <param name="position">Point in window pixels.</param>
    /// <param name="inheritedClip">Clip inherited from ancestors.</param>
    /// <returns>The topmost hit element, or null.</returns>
    private static UIElement? HitTest(UIElement element, Vector2 position, UIClipRect? inheritedClip)
        => HitTestLayer(element, position, inheritedClip, overlayOnly: true, inheritedOverlay: false) ??
            HitTestLayer(element, position, inheritedClip, overlayOnly: false, inheritedOverlay: false);

    /// <summary>Finds the topmost visible element in one composition layer.</summary>
    /// <param name="element">Subtree root.</param>
    /// <param name="position">Point in window pixels.</param>
    /// <param name="inheritedClip">Clip inherited from ancestors.</param>
    /// <param name="overlayOnly">Whether to consider the overlay layer instead of ordinary content.</param>
    /// <param name="inheritedOverlay">Whether an ancestor establishes overlay composition.</param>
    /// <returns>The topmost hit element in the requested layer, or null.</returns>
    private static UIElement? HitTestLayer(
        UIElement element,
        Vector2 position,
        UIClipRect? inheritedClip,
        bool overlayOnly,
        bool inheritedOverlay)
    {
        if (!element.IsVisible || !element.IsEnabled)
            return null;

        var overlay = inheritedOverlay || element.IsOverlay;
        if (!overlayOnly && overlay)
            return null;

        var bounds = new UIClipRect(element.Left, element.Top, element.Right, element.Bottom);
        var effectiveClip = element.ClipToBounds
            ? inheritedClip is { } parentClip
                ? UIClipRect.Intersect(parentClip, bounds)
                : bounds
            : inheritedClip;
        if (effectiveClip is { } clip && (clip.IsEmpty || !clip.Contains(position.X, position.Y)))
            return null;

        for (var index = element.Children.Count - 1; index >= 0; index--)
        {
            if (element.Children[index] is UIElement child &&
                HitTestLayer(child, position, effectiveClip, overlayOnly, overlay) is { } childHit)
                return childHit;
        }

        return overlay == overlayOnly && element.IsHitTestVisible && element.ContainsPoint(position)
            ? element
            : null;
    }

    /// <summary>Owns allocation-reused route and event storage for one reentrancy depth.</summary>
    private sealed class RouteDispatchState
    {
        /// <summary>Gets the reusable target-to-root route buffer.</summary>
        public UIElement[] Route { get; private set; } = new UIElement[8];

        /// <summary>Gets reusable event arguments for this dispatch depth.</summary>
        public UIPointerEventArgs EventArgs { get; } = new();

        /// <summary>Gets reusable keyboard event arguments for this dispatch depth.</summary>
        public UIKeyEventArgs KeyEventArgs { get; } = new();

        /// <summary>Gets reusable committed-text event arguments for this dispatch depth.</summary>
        public UITextInputEventArgs TextEventArgs { get; } = new();

        /// <summary>Gets reusable input-method composition event arguments.</summary>
        public UITextCompositionEventArgs CompositionEventArgs { get; } = new();

        /// <summary>Gets reusable routed-command arguments for this dispatch depth.</summary>
        public UICommandEventArgs CommandEventArgs { get; } = new();

        /// <summary>Gets reusable drag event arguments for this dispatch depth.</summary>
        public UIDragEventArgs DragEventArgs { get; } = new();

        /// <summary>Grows the route buffer when a deeper visual tree is encountered.</summary>
        /// <param name="capacity">Required element capacity.</param>
        public void EnsureCapacity(int capacity)
        {
            if (capacity <= Route.Length)
                return;
            var route = Route;
            Array.Resize(ref route, Route.Length * 2);
            Route = route;
        }
    }
}
