﻿using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class DiceRenderer : MonoBehaviour
{
    // This list should match the dice variant enum
    public List<DiceRendererDice> diceVariantPrefabs;

    public RenderTexture renderTexture { get; private set; }
    public int layerIndex { get; private set; }

    /// <summary>
    /// Called after instantiation to setup the camera, render texture, etc...
    /// </sumary>
    protected void Setup(int index, int widthHeight)
    {
        layerIndex = LayerMask.NameToLayer("Dice 0") + index;
        renderTexture = new RenderTexture(widthHeight, widthHeight, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
        renderTexture.wrapMode = TextureWrapMode.Clamp;
        renderTexture.filterMode = FilterMode.Point;
        renderTexture.Create();
    }

    void OnDestroy()
    {
        if (renderTexture != null)
        {
            renderTexture.Release();
            renderTexture = null;
        }
    }
}