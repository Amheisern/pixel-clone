using Newtonsoft.Json;

namespace Dice
{
    [System.Serializable]
    public class EditDie
    {
        public string name;
        //public ulong deviceId;
        public string systemId;
        public int faceCount; // Which kind of dice this is
        public DieDesignAndColor designAndColor; // Physical look
        public int currentBehaviorIndex;

        [JsonIgnore]
        public Behaviors.EditBehavior currentBehavior;

        [JsonIgnore]
        // Helper getter
        public Die die => DicePool.Instance?.GetDieForEditDie(this);

        public void OnBeforeSerialize()
        {
            currentBehaviorIndex = AppDataSet.Instance.behaviors.IndexOf(currentBehavior);
        }

        public void OnAfterDeserialize()
        {
            if (currentBehaviorIndex >= 0 && currentBehaviorIndex < AppDataSet.Instance.behaviors.Count)
                currentBehavior = AppDataSet.Instance.behaviors[currentBehaviorIndex];
            else
                currentBehavior = null;
        }
    }
}