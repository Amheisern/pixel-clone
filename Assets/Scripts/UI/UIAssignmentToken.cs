﻿using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class UIAssignmentToken : MonoBehaviour
{
    [Header("Controls")]
    public Button deleteButton;
    public RawImage diePreview;
    public Button selectDieDropdown;
    public Text dieName;
    public Button selectBehaviorDropdown;
    public Text behaviorName;

    public Presets.EditDieAssignment editAssignment { get; private set; }
    public SingleDiceRenderer dieRenderer { get; private set; }

    public Button.ButtonClickedEvent onDelete => deleteButton.onClick;

    public void Setup(Presets.EditDieAssignment ass, System.Func<Die, bool> dieSelector)
    {
        this.editAssignment = ass;
        Die die = null;
        if (this.editAssignment.die != null)
        {
            die = DicePool.Instance.FindDie(this.editAssignment.die);
        }

        selectDieDropdown.onClick.AddListener(() => PixelsApp.Instance.ShowDiePicker(
            "Select Die",
            die,
            dieSelector,
            OnDieSelected));
        selectBehaviorDropdown.onClick.AddListener(() => PixelsApp.Instance.ShowBehaviorPicker("Select Behavior", this.editAssignment.behavior, OnBehaviorSelected));
        UpdateView();
    }

    void OnDestroy()
    {
        if (this.dieRenderer != null)
        {
            DiceRendererManager.Instance.DestroyDiceRenderer(this.dieRenderer);
            this.dieRenderer = null;
        }
    }

    void OnDieSelected(bool result, Die newDie)
    {
        if (result)
        {
            editAssignment.die = AppDataSet.Instance.FindDie(newDie);
            UpdateView();
        }
    }

    void OnBehaviorSelected(bool result, Behaviors.EditBehavior newBehavior)
    {
        if (result)
        {
            editAssignment.behavior = newBehavior;
            UpdateView();
        }
    }

    void UpdateView()
    {
        if (this.dieRenderer != null)
        {
            DiceRendererManager.Instance.DestroyDiceRenderer(this.dieRenderer);
            this.dieRenderer = null;
        }

        var design = DiceVariants.DesignAndColor.Unknown;
        var dName = "Missing Die";
        if (editAssignment.die != null)
        {
            design = editAssignment.die.designAndColor;
            dName = editAssignment.die.name;
        }

        var bName = "Missing Behavior";
        if (editAssignment.behavior != null)
        {
            bName = editAssignment.behavior.name;
        }
        
        this.dieRenderer = DiceRendererManager.Instance.CreateDiceRenderer(design);
        if (dieRenderer != null)
        {
            diePreview.texture = dieRenderer.renderTexture;
        }
        dieRenderer.SetAuto(true);
        if (editAssignment.behavior != null)
        {
            dieRenderer.SetAnimations(editAssignment.behavior.CollectAnimations());
            dieRenderer.Play(true);
        }

        dieName.text = dName;
        behaviorName.text = bName;
    }
}