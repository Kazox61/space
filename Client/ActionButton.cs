using Godot;
using System;

[GlobalClass]
public partial class ActionButton : Button
{
    [Export(PropertyHint.InputName, "show_builtin,loose_mode")]
    public StringName action;

    public override void _EnterTree()
    {
        ButtonDown += InstantiateActionPress;
        ButtonUp += InstantiateActionRelease;
    }

    public override void _ExitTree()
    {
        ButtonDown -= InstantiateActionPress;
        ButtonUp -= InstantiateActionRelease;
    }

    private void InstantiateActionPress()
    {
        InstantiateAction(true);
    }

    private void InstantiateActionRelease()
    {
        InstantiateAction(false);
    }

    private void InstantiateAction(bool pressed)
    {
        if (pressed)
        {
            Input.ActionPress(action);
        }
        else
        {
            Input.ActionRelease(action);
        }
        var iea = new InputEventAction();
        iea.SetAction(action);
        iea.SetPressed(pressed);
        GetViewport().PushInput(iea, pressed);
    }
}
