using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Presets;
using Dice;
using System.Linq;

public class ActiveBehaviorMonitor : MonoBehaviour
{
    List<EditDie> connectedDice = new List<EditDie>();

    // Start is called before the first frame update
    void Awake()
    {
        PixelsApp.Instance.onDieBehaviorUpdatedEvent += OnBehaviorDownloadedEvent;
    }

    void OnBehaviorDownloadedEvent(Dice.EditDie die, Behaviors.EditBehavior behavior)
    {
        // Check whether we should stay connected to some of the dice
        var toDisconnect = new List<EditDie>(connectedDice);
        if (behavior.CollectAudioClips().Any())
        {
            // This die assignment uses a behavior that has audio clips, so stay connected to the die
            if (connectedDice.Contains(die))
            {
                toDisconnect.Remove(die);
            }
            else if (die != null)
            {
                // Connect to the new die
                connectedDice.Add(die);
                DicePool.Instance.ConnectDice(new[] { die }, () => !gameObject.activeInHierarchy, (d, res, _) =>
                {
                    if (!res)
                    {
                        connectedDice.Remove(d);
                    }
                });
            }
        }

        foreach (var d in toDisconnect)
        {
            connectedDice.Remove(d);
            DicePool.Instance.DisconnectDie(d);
        }
    }
}
