using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Dice;
using Presets;

public class UIPairedDieView : MonoBehaviour
{
    // Controls
    [Header("Controls")]
    public RawImage dieRenderImage;
    public Text dieNameText;
    public Text dieIDText;
    public Text firmwareIDText;
    public UIDieLargeBatteryView batteryView;
    public UIDieLargeSignalView signalView;
    public Text statusText;
    public RectTransform disconnectedTextRoot;
    public RectTransform errorTextRoot;

    [Header("Parameters")]
    public Color defaultTextColor;
    public Color selectedColor;


    public EditDie die { get; private set; }
    public SingleDiceRenderer dieRenderer { get; private set; }
    public bool selected { get; private set; }

    bool visible = true;

    public void Setup(EditDie die)
    {
        this.die = die;
        dieRenderer = DiceRendererManager.Instance.CreateDiceRenderer(die.designAndColor);
        if (dieRenderer != null)
        {
            dieRenderImage.texture = dieRenderer.renderTexture;
        }
        UpdateState();
        SetSelected(false);

        if (die.die != null)
        {
            OnDieFound(die);
        }
    }

    public void SetSelected(bool selected)
    {
        this.selected = selected;
        if (selected)
        {
            dieNameText.color = selectedColor;
        }
        else
        {
            dieNameText.color = defaultTextColor;
        }
    }

    public void UpdateState()
    {
        dieNameText.text = die.name;
        if (die.deviceId != 0)
        {
            dieIDText.text = "ID: " + die.deviceId.ToString("X08");
        }
        else
        {
            dieIDText.text = "ID: Unavailable";
        }

        if (die.die == null)
        {
            batteryView.SetLevel(null, null);
            signalView.SetRssi(null);
            dieRenderer.SetAuto(false);
            dieRenderImage.color = Color.white;
            batteryView.gameObject.SetActive(true);
            signalView.gameObject.SetActive(true);
            statusText.text = "Disconnected";
            disconnectedTextRoot.gameObject.SetActive(false);
            errorTextRoot.gameObject.SetActive(false);
            firmwareIDText.text = "Firmware: Unavailable";
        }
        else
        {
            firmwareIDText.text = "Firmware: " + die.die.firmwareVersionId;
            batteryView.SetLevel(die.die.batteryLevel, die.die.charging);
            signalView.SetRssi(die.die.rssi);
            switch (die.die.lastError)
            {
                case DieLastError.None:
                    switch (die.die.connectionState)
                    {
                    case DieConnectionState.Invalid:
                        dieRenderer.SetAuto(false);
                        dieRenderImage.color = AppConstants.Instance.DieUnavailableColor;
                        batteryView.gameObject.SetActive(false);
                        signalView.gameObject.SetActive(false);
                        statusText.text = "Invalid";
                        disconnectedTextRoot.gameObject.SetActive(true);
                        errorTextRoot.gameObject.SetActive(false);
                        break;
                    case DieConnectionState.Available:
                        dieRenderer.SetAuto(true);
                        dieRenderImage.color = Color.white;
                        batteryView.gameObject.SetActive(true);
                        signalView.gameObject.SetActive(true);
                        statusText.text = "Available";
                        disconnectedTextRoot.gameObject.SetActive(false);
                        errorTextRoot.gameObject.SetActive(false);
                        break;
                    case DieConnectionState.Connecting:
                        dieRenderer.SetAuto(false);
                        dieRenderImage.color = Color.white;
                        batteryView.gameObject.SetActive(true);
                        signalView.gameObject.SetActive(true);
                        statusText.text = "Identifying";
                        disconnectedTextRoot.gameObject.SetActive(false);
                        errorTextRoot.gameObject.SetActive(false);
                        break;
                    case DieConnectionState.Identifying:
                        dieRenderer.SetAuto(true);
                        dieRenderImage.color = Color.white;
                        batteryView.gameObject.SetActive(true);
                        signalView.gameObject.SetActive(true);
                        statusText.text = "Identifying";
                        disconnectedTextRoot.gameObject.SetActive(false);
                        errorTextRoot.gameObject.SetActive(false);
                        break;
                    case DieConnectionState.Ready:
                        dieRenderer.SetAuto(true);
                        dieRenderImage.color = Color.white;
                        batteryView.gameObject.SetActive(true);
                        signalView.gameObject.SetActive(true);
                        statusText.text = "Ready";
                        disconnectedTextRoot.gameObject.SetActive(false);
                        errorTextRoot.gameObject.SetActive(false);
                        break;
                    }
                    break;
                case DieLastError.ConnectionError:
                    dieRenderer.SetAuto(false);
                    dieRenderImage.color = AppConstants.Instance.DieUnavailableColor;
                    batteryView.gameObject.SetActive(false);
                    signalView.gameObject.SetActive(false);
                    statusText.text = "Connection Error";
                    disconnectedTextRoot.gameObject.SetActive(false);
                    errorTextRoot.gameObject.SetActive(true);
                    break;
                case DieLastError.Disconnected:
                    dieRenderer.SetAuto(false);
                    dieRenderImage.color = AppConstants.Instance.DieUnavailableColor;
                    batteryView.gameObject.SetActive(false);
                    signalView.gameObject.SetActive(false);
                    statusText.text = "Disconnected";
                    disconnectedTextRoot.gameObject.SetActive(true);
                    errorTextRoot.gameObject.SetActive(false);
                    break;
            }
        }
    }

    void Awake()
    {
        DicePool.onDieFound += OnDieFound;
        DicePool.onDieWillBeLost += OnDieWillBeLost;
    }

    void OnDestroy()
    {
        if (dieRenderer != null)
        {
            DiceRendererManager.Instance.DestroyDiceRenderer(dieRenderer);
            dieRenderer = null;
        }

        DicePool.onDieFound -= OnDieFound;
        DicePool.onDieWillBeLost -= OnDieWillBeLost;

        if (die.die != null)
        {
            OnDieWillBeLost(die);
        }
    }

    void OnConnectionStateChanged(Die die, DieConnectionState oldState, DieConnectionState newState)
    {
        UpdateState();
    }

    void OnError(Die die, DieLastError lastError)
    {
        UpdateState();
    }

    void OnBatteryLevelChanged(Die die, float? level, bool? charging)
    {
        UpdateState();
    }

    void OnRssiChanged(Die die, int? rssi)
    {
        UpdateState();
    }

    void OnNameChanged(Die die, string newName)
    {
        this.die.name = die.name;
        UpdateState();
    }

    void OnAppearanceChanged(Die die, int newFaceCount, DieDesignAndColor newDesign)
    {
        this.die.designAndColor = newDesign;
        if (dieRenderer != null)
        {
            DiceRendererManager.Instance.DestroyDiceRenderer(dieRenderer);
            dieRenderer = null;
        }
        dieRenderer = DiceRendererManager.Instance.CreateDiceRenderer(newDesign);
        if (dieRenderer != null)
        {
            dieRenderImage.texture = dieRenderer.renderTexture;
        }
    }

    void OnDieFound(EditDie editDie)
    {
        Debug.Assert(editDie == die);
        die.die.OnConnectionStateChanged += OnConnectionStateChanged;
        die.die.OnError += OnError;
        die.die.OnAppearanceChanged += OnAppearanceChanged;
        die.die.OnBatteryLevelChanged += OnBatteryLevelChanged;
        die.die.OnRssiChanged += OnRssiChanged;

        bool saveUpdatedData = false;
        if (die.designAndColor != die.die.designAndColor)
        {
            OnAppearanceChanged(die.die, die.die.faceCount, die.die.designAndColor);
            saveUpdatedData = true;
        }

        if (die.name != die.die.name)
        {
            OnNameChanged(die.die, die.die.name);
            saveUpdatedData = true;
        }

        if (saveUpdatedData)
        {
            AppDataSet.Instance.SaveData();
        }
    }

    void OnDieWillBeLost(EditDie editDie)
    {
        editDie.die.OnConnectionStateChanged -= OnConnectionStateChanged;
        editDie.die.OnAppearanceChanged -= OnAppearanceChanged;
        editDie.die.OnBatteryLevelChanged -= OnBatteryLevelChanged;
        editDie.die.OnRssiChanged -= OnRssiChanged;
        editDie.die.OnError -= OnError;
    }

    void Update()
    {
        bool newVisible = GetComponent<RectTransform>().IsVisibleFrom();
        if (newVisible != visible)
        {
            visible = newVisible;
            DiceRendererManager.Instance.OnDiceRendererVisible(dieRenderer, visible);
        }
    }

}
