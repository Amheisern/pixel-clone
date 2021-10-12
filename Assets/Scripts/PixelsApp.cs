﻿using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.Linq;
using Dice;
using Animations;
using Behaviors;
using System.IO;
using SimpleFileBrowser;

public class PixelsApp : SingletonMonoBehaviour<PixelsApp>
{
    [Header("Panels")]
    public UIDialogBox dialogBox;
    public UIColorPicker colorPicker;
    public UIAnimationPicker animationPicker;
    public UIDiePicker diePicker;
    public UIBehaviorPicker behaviorPicker;
    public UIFacePicker facePicker;
    public UIGradientEditor gradientEditor;
    public UIEnumPicker enumPicker;
    public UIProgrammingBox programmingBox;
    public UIPatternEditor patternEditor;
    public UIPatternPicker patternPicker;
    public UIAudioClipPicker audioClipPicker;

    public delegate void OnDieBehaviorUpdatedEvent(Dice.EditDie die, Behaviors.EditBehavior behavior);
    public OnDieBehaviorUpdatedEvent onDieBehaviorUpdatedEvent;

    [Header("Controls")]
    public UIMainMenu mainMenu;

    public void ShowMainMenu()
    {
        mainMenu.Show();
    }

    public void HideMainMenu()
    {
        mainMenu.Hide();
    }

    public bool ShowDialogBox(string title, string message, string okMessage = "Ok", string cancelMessage = null, System.Action<bool> closeAction = null)
    {
        bool ret = !dialogBox.isShown;
        if (ret)
        {
            dialogBox.Show(title, message, okMessage, cancelMessage, closeAction);
        }
        return ret;
    }

    public bool ShowColorPicker(string title, Color previousColor, System.Action<bool, Color> closeAction)
    {
        bool ret = !colorPicker.isShown;
        if (ret)
        {
            colorPicker.Show(title, previousColor, closeAction);
        }
        return ret;
    }

    public bool ShowAnimationPicker(string title, Animations.EditAnimation previousAnimation, System.Action<bool, Animations.EditAnimation> closeAction)
    {
        bool ret = !animationPicker.isShown;
        if (ret)
        {
            animationPicker.Show(title, previousAnimation, closeAction);
        }
        return ret;
    }

    public bool ShowDiePicker(string title, Dice.EditDie previousDie, System.Func<Dice.EditDie, bool> selector, System.Action<bool, Dice.EditDie> closeAction)
    {
        bool ret = !diePicker.isShown;
        if (ret)
        {
            diePicker.Show(title, previousDie, selector, closeAction);
        }
        return ret;
    }

    public bool ShowBehaviorPicker(string title, Behaviors.EditBehavior previousBehavior, System.Action<bool, Behaviors.EditBehavior> closeAction)
    {
        bool ret = !behaviorPicker.isShown;
        if (ret)
        {
            behaviorPicker.Show(title, previousBehavior, closeAction);
        }
        return ret;
    }

    public bool ShowFacePicker(string title, int previousFaceMask, System.Action<bool, int> closeAction)
    {
        bool ret = !facePicker.isShown;
        if (ret)
        {
            facePicker.Show(title, previousFaceMask, closeAction);
        }
        return ret;
    }

    public bool ShowGradientEditor(string title, Animations.EditRGBGradient previousGradient, System.Action<bool, Animations.EditRGBGradient> closeAction)
    {
        bool ret = !gradientEditor.isShown;
        if (ret)
        {
            gradientEditor.Show(title, previousGradient, closeAction);
        }
        return ret;
    }

    public bool ShowEnumPicker(string title, System.Enum previousValue, System.Action<bool, System.Enum> closeAction, List<System.Enum> validValues)
    {
        bool ret = !enumPicker.isShown;
        if (ret)
        {
            enumPicker.Show(title, previousValue, closeAction, validValues);
        }
        return ret;
    }

    public bool ShowPatternEditor(string title, Animations.EditPattern previousPattern, System.Action<bool, Animations.EditPattern> closeAction)
    {
        bool ret = !patternEditor.isShown;
        if (ret)
        {
            patternEditor.Show(title, previousPattern, closeAction);
        }
        return ret;
    }

    public bool ShowPatternPicker(string title, Animations.EditPattern previousPattern, System.Action<bool, Animations.EditPattern> closeAction)
    {
        bool ret = !patternPicker.isShown;
        if (ret)
        {
            patternPicker.Show(title, previousPattern, closeAction);
        }
        return ret;
    }

    public bool ShowAudioClipPicker(string title, AudioClipManager.AudioClipInfo previousClip, System.Action<bool, AudioClipManager.AudioClipInfo> closeAction)
    {
        bool ret = !audioClipPicker.isShown;
        if (ret)
        {
            audioClipPicker.Show(title, previousClip, closeAction);
        }
        return ret;
    }

    public bool ShowProgrammingBox(string description)
    {
        bool ret = !programmingBox.isShown;
        if (ret)
        {
            programmingBox.Show(description);
        }
        return ret;
    }

    public bool UpdateProgrammingBox(float percent, string description = null)
    {
        bool ret = programmingBox.isShown;
        if (ret)
        {
            programmingBox.SetProgress(percent, description);
        }
        return ret;
    }

    public bool HideProgrammingBox()
    {
        bool ret = programmingBox.isShown;
        if (ret)
        {
            programmingBox.Hide();
        }
        return ret;
    }

    public void ActivateBehavior(Behaviors.EditBehavior behavior, System.Action<EditDie, bool> callback = null)
    {
        // Select the die
        ShowDiePicker("Select Die", null, null, (res, selectedDie) =>
        {
            if (res)
            {
                // Attempt to activate the behavior on the die
                UploadBehavior(behavior, selectedDie, (res2) =>
                {
                    callback?.Invoke(selectedDie, res2);
                });
            }
        });
    }

    public void UpdateDieDataSet(Presets.EditDieAssignment editDieAssignment, System.Action<bool> callback = null)
    {
        UpdateDieDataSet(editDieAssignment.behavior, editDieAssignment.die, callback);
    }

    public void UpdateDieDataSet(EditBehavior behavior, EditDie die, System.Action<bool> callback = null)
    {
        // Make sure the die is ready!
        ShowProgrammingBox($"Connecting to {die.name}...");

        DicePool.Instance.ConnectDice(new[] { die }, () => !gameObject.activeInHierarchy, (editDie, res, err) =>
        {
            if (gameObject.activeInHierarchy && res)
            {
                // Upload the behavior data
                StartCoroutine(UploadCr());

                IEnumerator UploadCr()
                {
                    bool success = false;
                    string errorTitle = null;
                    string error = null;

                    try
                    {
                        // The die is ready to be uploaded to
                        var dataSet = behavior.ToEditSet().ToDataSet();

                        // Get the hash directly from the die
                        yield return editDie.die.GetDieInfoAsync((res, err) => (success, error) = (res, err));

                        if (success)
                        {
                            // Check the dataset against the one stored in the die
                            var hash = dataSet.ComputeHash();

                            if (hash != editDie.die.dataSetHash)
                            {
                                // We need to upload the dataset first
                                Debug.Log("Uploading dataset to die " + editDie.name);
                                var dataSetDataSize = dataSet.ComputeDataSetDataSize();

                                Debug.Log("Dataset data size " + dataSetDataSize);
                                UpdateProgrammingBox(0.0f, $"Uploading data to {editDie.name}...");

                                // Upload dataset
                                success = false;
                                error = null;
                                StartCoroutine(editDie.die.UploadDataSetAsync(
                                    dataSet,
                                    (res, err) => (success, error) = (res, err),
                                    progress => UpdateProgrammingBox(progress, $"Uploading data to {editDie.name}...")));

                                yield return new WaitUntil(() => success || (error != null));

                                if (success)
                                {
                                    // Check hash returned from die
                                    success = false;
                                    error = null;
                                    yield return editDie.die.GetDieInfoAsync((res, err) => (success, error) = (res, err));

                                    if (success)
                                    {
                                        // Check hash
                                        if (hash != editDie.die.dataSetHash)
                                        {
                                            errorTitle = "Error verifying data sent to " + editDie.name;
                                        }
                                        else
                                        {
                                            die.currentBehavior = behavior;
                                            onDieBehaviorUpdatedEvent?.Invoke(die, die.currentBehavior);
                                            success = true;
                                        }
                                    }
                                    else
                                    {
                                        errorTitle = "Error fetching profile hash value from " + editDie.name;
                                    }
                                }
                                else
                                {
                                    errorTitle = "Error uploading data to " + editDie.name;
                                }
                            }
                            else
                            {
                                Debug.Log($"Die {editDie.name} already has preset with hash 0x{hash:X8} programmed");
                                errorTitle = "Profile already Programmed";
                                error = $"Die {editDie.name} already has profile \"{behavior.name}\" programmed.";
                                success = true;
                            }
                        }
                        else
                        {
                            errorTitle = "Error verifying profile hash on " + editDie.name;
                        }
                    }
                    finally
                    {
                        HideProgrammingBox();
                        DicePool.Instance.DisconnectDie(editDie);
                    }

                    if (error != null)
                    {
                        if (!success)
                        {
                            Debug.LogError($"Error sending data set {behavior.name} to die {editDie.name}: {errorTitle}, {error}");
                        }

                        // We may still have a message to show even if the operation was successful
                        ShowDialogBox(errorTitle, error);
                    }
                    callback?.Invoke(success);
                }
            }
            else
            {
                HideProgrammingBox();
                ShowDialogBox("Error connecting to " + editDie.name, err);
                callback(false);
            }
        });
    }

    public void UploadPreset(Presets.EditPreset editPreset, System.Action<bool> callback = null)
    {
        int currentAssignment = 0;
        void updateNextDie()
        {
            UpdateDieDataSet(editPreset.dieAssignments[currentAssignment], (res) =>
            {
                if (res)
                {
                    currentAssignment++;
                    if (currentAssignment < editPreset.dieAssignments.Count)
                    {
                        updateNextDie();
                    }
                    else
                    {
                        // We're done!
                        callback?.Invoke(true);
                    }
                }
                else
                {
                    callback?.Invoke(false);
                }
            });
        }

        // Kick off the upload chain
        if (editPreset.dieAssignments.Count > 0)
        {
            updateNextDie();
        }
        else
        {
            callback?.Invoke(false);
        }
    }

    public void UploadBehavior(Behaviors.EditBehavior behavior, Dice.EditDie die, System.Action<bool> callback = null)
    {
        UpdateDieDataSet(behavior, die, (res) =>
        {
            if (res)
            {
                // We're done!
                callback?.Invoke(true);
            }
            else
            {
                callback?.Invoke(false);
            }
        });
    }

    public void RestartTutorial()
    {
        AppSettings.Instance.EnableAllTutorials();
        Tutorial.Instance.StartMainTutorial();
    }

    public void ImportPattern()
    {
        static void FileSelected(string filePathname)
        {
            if (!string.IsNullOrEmpty(filePathname))
            {
                Debug.Log("Selected JSON pattern file: " + filePathname);
                // Load the pattern from JSON
                AppDataSet.Instance.ImportAnimation(filePathname);
            }
        }

#if UNITY_EDITOR
        FileSelected(UnityEditor.EditorUtility.OpenFilePanel("Select JSON Pattern", "", "json"));
#elif UNITY_STANDALONE_WIN
        // Set filters (optional)
		// It is sufficient to set the filters just once (instead of each time before showing the file browser dialog), 
		// if all the dialogs will be using the same filters
		FileBrowser.SetFilters( true, new FileBrowser.Filter( "JSON", ".json" ));

		// Set default filter that is selected when the dialog is shown (optional)
		// Returns true if the default filter is set successfully
		// In this case, set Images filter as the default filter
		FileBrowser.SetDefaultFilter( ".json" );
        FileBrowser.ShowLoadDialog((paths) => FileSelected(paths[0]), null, FileBrowser.PickMode.Files, false, null, null, "Select JSON", "Select");
#else
        NativeFilePicker.PickFile(FileSelected, new string[] { NativeFilePicker.ConvertExtensionToFileType("json") });
#endif
    }

    public void ExportPattern(EditAnimation animation)
    {
        void FileSelected(string filePathname, System.Action onDone = null)
        {
            if (!string.IsNullOrEmpty(filePathname))
            {
                Debug.Log("Exporting pattern " + animation.name);
                // Save the pattern to JSON
                AppDataSet.Instance.ExportAnimation(animation, filePathname);
                onDone?.Invoke();
            }
        }

#if UNITY_EDITOR
        FileSelected(UnityEditor.EditorUtility.SaveFilePanel("Export Pattern", "", animation.name, "json"));
#elif UNITY_STANDALONE_WIN
        // Set filters (optional)
        // It is sufficient to set the filters just once (instead of each time before showing the file browser dialog), 
        // if all the dialogs will be using the same filters
        FileBrowser.SetFilters( true, new FileBrowser.Filter( "JSON", ".json" ));

		// Set default filter that is selected when the dialog is shown (optional)
		// Returns true if the default filter is set successfully
		// In this case, set Images filter as the default filter
		FileBrowser.SetDefaultFilter( ".json" );
        FileBrowser.ShowSaveDialog((paths) => FileSelected(paths[0]), null, FileBrowser.PickMode.Files, false, null, null, "Save JSON", "Select");
#else
        string jsonPathname = Path.Combine(Application.persistentDataPath, animation.name + ".json");
        FileSelected(jsonPathname, () =>
            NativeFilePicker.ExportFile(jsonPathname, res =>
            {
                if (!res)
                {
                    Debug.LogError("Error exporting animation to JSON");
                }
                File.Delete(jsonPathname);
            }));
#endif
    }

    public void ImportUserData()
    {
        void FileSelected(string filePathname)
        {
            if (!string.IsNullOrEmpty(filePathname))
            {
                ShowDialogBox("Replace Settings?", "Presets, Lighting Patterns, LED Patterns and Profiles will be replaced. Imported Audio Clips will be kept.", "Yes", "No", res =>
                {
                    if (res)
                    {
                        ShowDialogBox("Last Chance!", "Replace settings?\nThe app will close when done.", "Yes", "No", res2 =>
                        {
                            if (res)
                            {
                                Debug.Log("Replacing user data with contents from file: " + filePathname);
                                if (File.Exists(AppDataSet.Instance.pathname))
                                {
                                    File.Delete(AppDataSet.Instance.pathname);
                                }
                                File.Copy(filePathname, AppDataSet.Instance.pathname);
                                Debug.LogWarning("Exiting!");
                                Application.Quit();
                            }
                        });
                    }
                });
            }
        }

#if UNITY_EDITOR
        FileSelected(UnityEditor.EditorUtility.OpenFilePanel("Select JSON settings", "", "json"));
#elif UNITY_STANDALONE_WIN
        // Set filters (optional)
		// It is sufficient to set the filters just once (instead of each time before showing the file browser dialog), 
		// if all the dialogs will be using the same filters
		FileBrowser.SetFilters( true, new FileBrowser.Filter( "JSON", ".json" ));

		// Set default filter that is selected when the dialog is shown (optional)
		// Returns true if the default filter is set successfully
		// In this case, set Images filter as the default filter
		FileBrowser.SetDefaultFilter( ".json" );
        FileBrowser.ShowLoadDialog((paths) => FileSelected(paths[0]), null, FileBrowser.PickMode.Files, false, null, null, "Select JSON", "Select");
#else
        NativeFilePicker.PickFile(FileSelected, new string[] { NativeFilePicker.ConvertExtensionToFileType("json") });
#endif
    }

    public void ExportUserData()
    {
        static void FileSelected(string filePathname, System.Action onDone = null)
        {
            if (!string.IsNullOrEmpty(filePathname))
            {
                Debug.Log("Copying user data file to: " + filePathname);
                if (File.Exists(filePathname))
                {
                    File.Delete(filePathname);
                }
                File.Copy(AppDataSet.Instance.pathname, filePathname);
                onDone?.Invoke();
            }
        }

#if UNITY_EDITOR
        FileSelected(UnityEditor.EditorUtility.SaveFilePanel("Export settings", "", "PixelsSettingsBackup", "json"));
#elif UNITY_STANDALONE_WIN
        // Set filters (optional)
        // It is sufficient to set the filters just once (instead of each time before showing the file browser dialog), 
        // if all the dialogs will be using the same filters
        FileBrowser.SetFilters( true, new FileBrowser.Filter( "JSON", ".json" ));

		// Set default filter that is selected when the dialog is shown (optional)
		// Returns true if the default filter is set successfully
		// In this case, set Images filter as the default filter
		FileBrowser.SetDefaultFilter( ".json" );
        FileBrowser.ShowSaveDialog((paths) => FileSelected(paths[0]), null, FileBrowser.PickMode.Files, false, null, null, "Save JSON", "Select");
#else
        string jsonPathname = Path.Combine(Application.persistentDataPath, "PixelsSettingsBackup.json");
        FileSelected(jsonPathname, () =>
            NativeFilePicker.ExportFile(jsonPathname, res =>
            {
                if (!res)
                {
                    Debug.LogError("Error exporting settings to JSON");
                }
                File.Delete(jsonPathname);
            }));
#endif
    }

    public void RestoreDefaultSettings()
    {
        ShowDialogBox("Restore Default Settings?", "All imported Audio Clips and LED Patterns will be lost. Presets, Lighting Patterns and Profiles will be restored to the app defaults. All Dice pairing information will be removed.", "Yes", "No", res =>
        {
            if (res)
            {
                ShowDialogBox("Last Chance!", "Restore default settings?\nThe app will close when done.", "Yes", "No", res2 =>
                {
                    if (res)
                    {
                        static void DeleteFile(string pathname)
                        {
                            if (File.Exists(pathname))
                            {
                                Debug.LogWarning("Deleting " + pathname);
                                try
                                {
                                    File.Delete(pathname);
                                }
                                catch (System.Exception e)
                                {
                                    Debug.LogException(e);
                                }
                            }
                        }

                        DeleteFile(AppSettings.Instance.pathname);
                        DeleteFile(AppDataSet.Instance.pathname);
                        AudioClipManager.Instance.DeleteAllUserClipFiles();
                        Debug.LogWarning("Exiting!");
                        Application.Quit();
                    }
                });
            }
        });
    }

    public void ExportLogFiles()
    {

        static void FileSelected(string filePathname, System.Action onDone = null)
        {
            if (!string.IsNullOrEmpty(filePathname))
            {
                try
                {
                    if (File.Exists(filePathname))
                    {
                        File.Delete(filePathname);
                    }

                    // Suspend logger before reading the file because ZipFile.CreateFromDirectory
                    // throws and exception if the file is opened with "write" attribute
                    CustomLogger.Instance.Suspend(_ =>
                    {
                        Debug.Log("Archiving logs");
                        // Archive all logs in zip file
                        System.IO.Compression.ZipFile.CreateFromDirectory(CustomLogger.LogsDirectory, filePathname);
                    });
                    onDone?.Invoke();
                }
                catch (System.Exception e)
                {
                    Debug.LogException(e);
                }
            }
        }

#if UNITY_EDITOR
        FileSelected(UnityEditor.EditorUtility.SaveFilePanel("Export Log Files", "", "logs", "zip"));
#elif UNITY_STANDALONE_WIN
        // Set filters (optional)
        // It is sufficient to set the filters just once (instead of each time before showing the file browser dialog), 
        // if all the dialogs will be using the same filters
        FileBrowser.SetFilters( true, new FileBrowser.Filter( "Zip Archive", ".zip" ));

		// Set default filter that is selected when the dialog is shown (optional)
		// Returns true if the default filter is set successfully
		// In this case, set Images filter as the default filter
		FileBrowser.SetDefaultFilter( ".zip" );
        FileBrowser.ShowSaveDialog((paths) => FileSelected(paths[0]), null, FileBrowser.PickMode.Files, false, null, null, "Export Log Files", "Select");
#else
        string archivePathname = Path.Combine(Application.persistentDataPath, "logs.zip");
        FileSelected(archivePathname, () =>
            NativeFilePicker.ExportFile(archivePathname, res =>
            {
                if (!res)
                {
                    Debug.LogError("Error exporting logs archive");
                }
                File.Delete(archivePathname);
            }));
#endif
    }

    // Start is called before the first frame update
    IEnumerator Start()
    {
        Debug.Log($"Running app version {AppConstants.Instance.AppVersion}");

        while (!Systemic.Pixels.Unity.BluetoothLE.Central.IsReady) yield return null;

        // Pretend to have updated the current preset on load
        foreach (var die in AppDataSet.Instance.dice)
        {
            if (die.currentBehavior != null)
            {
                onDieBehaviorUpdatedEvent?.Invoke(die, die.currentBehavior);
            }
        }
    }
}
