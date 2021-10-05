using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Dice;
using System.Linq;
using System.Text;

public class UIPairedDieToken : MonoBehaviour
{
    // Controls
    [Header("Controls")]
    public Button mainButton;
    public Image backgroundImage;
    public Button expandButton;
    public Image expandButtonImage;
    public GameObject expandGroup;
    public UIPairedDieView dieView;

    [Header("ExpandedControls")]
    public Button statsButton;
    public Button renameButton;
    public Button forgetButton;
    public Button resetButton;
    public Button calibrateButton;
    public Button setDesignButton;
    public Button pingButton;
    public Button disconnectButton;

    [Header("Images")]
    public Sprite backgroundCollapsedSprite;
    public Sprite backgroundExpandedSprite;
    public Sprite buttonCollapsedSprite;
    public Sprite buttonExpandedSprite;

    public bool expanded => expandGroup.activeSelf;
    public EditDie die => dieView.die;

    IEnumerator refreshInfoCoroutine;

    public void Setup(EditDie die)
    {
        dieView.Setup(die);

        // Connect to all the dice in the pool if possible
        refreshInfoCoroutine = RefreshInfo();
        StartCoroutine(refreshInfoCoroutine);
    }

    void Awake()
    {
        // Hook up to events
        mainButton.onClick.AddListener(OnToggle);
        expandButton.onClick.AddListener(OnToggle);
        forgetButton.onClick.AddListener(OnForget);
        renameButton.onClick.AddListener(OnRename);
        calibrateButton.onClick.AddListener(OnCalibrate);
        setDesignButton.onClick.AddListener(OnSetDesign);
        pingButton.onClick.AddListener(OnPing);
        resetButton.onClick.AddListener(() => die.die.DebugAnimController());
        disconnectButton.onClick.AddListener(OnDisconnect);
    }

    void OnToggle()
    {
        bool newActive = !expanded;
        expandGroup.SetActive(newActive);
        backgroundImage.sprite = newActive ? backgroundExpandedSprite : backgroundCollapsedSprite;
        expandButtonImage.sprite = newActive ? buttonExpandedSprite : buttonCollapsedSprite;
    }

    void OnForget()
    {
        OnToggle();
        PixelsApp.Instance.ShowDialogBox(
            "Forget " + die.name + "?",
            "Are you sure you want to remove it from your dice bag?",
            "Forget",
            "Cancel",
            res =>
            {
                if (res)
                {
                    var dependentPresets = AppDataSet.Instance.CollectPresetsForDie(die);
                    if (dependentPresets.Any())
                    {
                        StringBuilder builder = new StringBuilder();
                        builder.Append("The following presets depend on ");
                        builder.Append(die.name);
                        builder.AppendLine(":");
                        foreach (var b in dependentPresets)
                        {
                            builder.Append("\t");
                            builder.AppendLine(b.name);
                        }
                        builder.Append("Are you sure you want to forget it?");

                        PixelsApp.Instance.ShowDialogBox("Die In Use!", builder.ToString(), "Ok", "Cancel", res2 =>
                        {
                            if (res2)
                            {
                                DicePool.Instance.ForgetDie(die);
                            }
                        });
                    }
                    else
                    {
                        DicePool.Instance.ForgetDie(die);
                    }
                }
            });
    }

    void OnRename()
    {
        OnToggle();
        if (die.die != null && die.die.connectionState == DieConnectionState.Ready)
        {
            var newName = Names.GetRandomName();
            die.die.RenameDie(newName, (res) =>
            {
                die.die.name = newName;
                die.name = newName;
                AppDataSet.Instance.SaveData();
                dieView.UpdateState();
            });
        }
    }

    void OnCalibrate()
    {
        OnToggle();
        if (die.die != null)
        {
            die.die.StartCalibration();
        }
    }

    void OnSetDesign()
    {
        OnToggle();
        if (die.die != null && die.die.connectionState == DieConnectionState.Ready)
        {
            PixelsApp.Instance.ShowEnumPicker("Select Design", die.designAndColor, (res, newDesign) =>
            {
                die.designAndColor = (Dice.DieDesignAndColor)newDesign;
                die.die.SetCurrentDesignAndColor((Dice.DieDesignAndColor)newDesign, (res2) =>
                {
                    if (res2)
                    {
                        AppDataSet.Instance.SaveData();
                        dieView.UpdateState();
                    }
                });
            },
            null);
        }
    }

    void OnPing()
    {
        OnToggle();
        if (die.die != null && die.die.connectionState == DieConnectionState.Ready)
        {
            die.die.Flash(Color.yellow, 3, null);
        }
    }

    void OnDisconnect()
    {
        OnToggle();
        if (die.die != null && die.die.isConnectingOrReady)
        {
            DicePool.Instance.DisconnectDie(die, forceDisconnect: true);
        }
    }

    IEnumerator RefreshInfo()
    {
        while (true)
        {
            int counter = 0;
            while (die.die?.connectionState == DieConnectionState.Ready)
            {
                if (counter == 0)
                {
                    // Fetch battery level every once in a while
                    bool battLevelReceived = false;
                    die.die.GetBatteryLevel((d, f) => battLevelReceived = true);
                    yield return new WaitUntil(() => battLevelReceived == true);
                }

                // Fetch RSSI every time
                bool rssiReceived = false;
                die.die.GetRssi((d, i) => rssiReceived = true);
                yield return new WaitUntil(() => rssiReceived == true);

                yield return new WaitForSeconds(3.0f);

                counter = (int)Mathf.Repeat(counter + 1, 4);
            }

            yield return null;
        }
    }
}
