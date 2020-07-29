﻿using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

public class AnimationPropertiesPanel : SingletonMonoBehaviour<AnimationPropertiesPanel>
{
	[SerializeField]
	InputField _nameInput = null;
	[SerializeField]
	Dropdown _roleDropdown = null;
	[SerializeField]
	Toggle _roleToggle = null;
	[SerializeField]
	Transform[] _toDisableOnShow = null;
    [SerializeField]
    Dropdown _specialColorDropdown = null;

    ApplyCallback _doneCb;
	UserActionCallback _userActionCb;

	public enum UserAction
	{
		None, Duplicate, Clear, Remove,
	}

	public delegate void ApplyCallback(string name, string role, Animations.SpecialColor specialColor);
	public delegate void UserActionCallback(UserAction action);

	public void Show(string name, Animations.SpecialColor specialColorType, ApplyCallback applyCallback, UserActionCallback userActionCallback)
	{
        _nameInput.text = name;
        _specialColorDropdown.value = (int)specialColorType;
        _doneCb = applyCallback;
		_userActionCb = userActionCallback;
		gameObject.SetActive(true);
        _nameInput.Select();
	}

	public void Apply()
	{
        string name = _nameInput.text;
        if (string.IsNullOrWhiteSpace(name))
        {
			name = string.Empty;
        }
		string role = _roleDropdown.interactable ? _roleDropdown.options[_roleDropdown.value].text : null;

        Close();
        _doneCb(name, role, (Animations.SpecialColor)_specialColorDropdown.value);
	}

	public void Close()
	{
		gameObject.SetActive(false);
	}

	public void AssignRole(bool yes)
	{
		_roleDropdown.interactable = yes;
	}

	public void DuplicateAnimation()
	{
		Close();
		_userActionCb(UserAction.Duplicate);
	}

	public void ClearAnimation()
	{
		Close();
		_userActionCb(UserAction.Clear);
	}

	public void RemoveAnimation()
	{
		Close();
		_userActionCb(UserAction.Remove);
	}

	void OnEnable()
	{
		foreach (var trans in _toDisableOnShow)
		{
			trans.gameObject.SetActive(false);
		}
	}
}
