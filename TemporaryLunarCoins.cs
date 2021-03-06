﻿using BepInEx;
using BepInEx.Configuration;
using MonoMod.Cil;
//using R2API;
using R2API.Utils;
using RoR2;
//using RoR2.Networking;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Networking;
//using Random = System.Random;

namespace blazingdrummer.TemporaryLunarCoins
{
	[BepInDependency("com.bepis.r2api")]
	[BepInPlugin("com.blazingdrummer.TemporaryLunarCoins", "TemporaryLunarCoins", "1.0.0")]
	public class TemporaryLunarCoins : BaseUnityPlugin
	{
		bool AllAgree = false;
		bool LoadingFromSave = false;
		static List<SteamPlayer> SteamPlayers = new List<SteamPlayer>();

		private static ConfigEntry<bool> ChangeDroprate;
		private static ConfigEntry<float> DropChance;
		private static ConfigEntry<float> DropMulti;

		public void Awake()
		{
			ChangeDroprate = Config.Bind("", "ChangeDroprate", true, new ConfigDescription("If this is set to false, it will ignore the other values and use vanilla settings."));
			DropChance = Config.Bind("", "DropChance", 2f, new ConfigDescription("The initial value to drop coins. Vanilla is 1 (percent)"));
			DropMulti = Config.Bind("", "DropMulti", 0.8f, new ConfigDescription("The multiplier for which, after every lunar coin is dropped, modifies the current dropchance. Results in diminishing returns. Vanilla  is 0.5 (percent)."));

			Run.onRunStartGlobal += delegate(Run run)
			{
				// TODO: remove logging before release
				Chat.AddMessage("Stages cleared: " + run.NetworkstageClearCount);
				// checking if this is a loaded game or not
				if (run.NetworkstageClearCount <= 0)
				{
					LoadingFromSave = false;
					AllAgree = false;
					SteamPlayers = PopulateSteamPlayersList();
					StartCoroutine(StartCoinRemovalAgreement());
				}
				else
				{
					LoadingFromSave = true;
				}
			};
			
			On.RoR2.Chat.UserChatMessage.ConstructChatString += UserChatMessage_ConstructChatString;

			// save file stores run's current coin amount and multiplier value
			// initial drop chance is the initial value of lunarCoinChanceMultiplier and the actual "multiplier" is hard-coded
			if (ChangeDroprate.Value)
			{
				// the IL edit to the hard-coded multiplier needs to happen every time, regardless of loading from save
				// Taken from LoonerCoins
				BindingFlags allFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
				var initDelegate = typeof(PlayerCharacterMasterController).GetNestedTypes(allFlags)[0].GetMethodCached(name: "<Init>b__61_0");
				MonoMod.RuntimeDetour.HookGen.HookEndpointManager.Modify(initDelegate, (Action<ILContext>)coinDropHook);

				// the drop chance should not be set back to the config value in the case of a loaded save
				On.RoR2.PlayerCharacterMasterController.Awake += PlayerCharacterMasterController_Awake;
			}
		}

		private void PlayerCharacterMasterController_Awake(On.RoR2.PlayerCharacterMasterController.orig_Awake orig, PlayerCharacterMasterController self)
		{
			orig(self);
			if (!LoadingFromSave)
			{
				// this sets the initial drop value; overrides the initial value of 0.5f set in PlayerCharacterMasterController()
				self.SetFieldValue("lunarCoinChanceMultiplier", DropChance.Value);
			}
		}

		private List<SteamPlayer> PopulateSteamPlayersList()
		{
			List<SteamPlayer> steamPlayers = new List<SteamPlayer>();
			for (int i = 0; i < PlayerCharacterMasterController.instances.Count; i++)
			{
				steamPlayers.Add(new SteamPlayer(PlayerCharacterMasterController.instances[i]));
			}

			return steamPlayers;
		}

		private string UserChatMessage_ConstructChatString(On.RoR2.Chat.UserChatMessage.orig_ConstructChatString orig, Chat.UserChatMessage self)
		{
			if (!LoadingFromSave)
			{
				if (!AllAgree && self.text.ToLower().Equals("agree"))
				{
					NetworkUser networkUser = self.sender.GetComponent<NetworkUser>();
					if (networkUser)
					{
						var steamID = networkUser.id.steamId;
						for (int i = 0; i < SteamPlayers.Count; i++)
						{
							if (SteamPlayers[i].steamID == steamID)
							{
								SteamPlayers[i].isReady = true;
								break;
							}
						}
						bool checkAllReady = true;
						for (int i = 0; i < SteamPlayers.Count; i++)
						{
							if (!SteamPlayers[i].isReady)
							{
								checkAllReady = false;
								break;
							}
						}
						if (checkAllReady)
						{
							//Time.timeScale = 1f;
							AllAgree = true;
							RemoveLunarCoins();
							UnfreezePlayers();
						}
					}
				}
			}
			return orig(self);
		}


		// Taken from LoonerCoins by Paddywaan
		// this section modifies the multiplier through IL; replaces the initial value of 0.5f in PlayerCharacterMasterController.Init()
		private void coinDropHook(ILContext il)
		{
			var c = new ILCursor(il);

			c.GotoNext(
				x => x.MatchDup(),
				x => x.MatchLdfld<PlayerCharacterMasterController>("lunarCoinChanceMultiplier"),
				x => x.MatchLdcR4(0.5f),
				x => x.MatchMul()
				);
			c.Index += 2;
			c.Next.Operand = DropMulti.Value;
			// Debug.Log(il);
		}


		private void UnfreezePlayers()
		{
			//Time.timeScale = 1f;
			foreach (var p in PlayerCharacterMasterController.instances)
			{
				p.master.GetBody().gameObject.GetComponent<SetStateOnHurt>().SetFrozen(0.1f);
				p.master.GetBody().RemoveBuff(BuffIndex.HiddenInvincibility);
				p.master.GetBody().AddTimedBuff(BuffIndex.HiddenInvincibility, 3f);
			}
		}

		private void RemoveLunarCoins()
		{
			for (int i = 0; i < PlayerCharacterMasterController.instances.Count; i++)
			{
				PlayerCharacterMasterController.instances[i].networkUser.DeductLunarCoins(PlayerCharacterMasterController.instances[i].networkUser.lunarCoins);
			}
		}

		public IEnumerator StartCoinRemovalAgreement()
		{
			yield return new WaitForSeconds(7f);
			//Time.timeScale = 0f;
			Chat.SendBroadcastChat(new Chat.SimpleChatMessage
			{
				baseToken = " -- Temporary Lunar Coins --" +
				"\n<size=15px>If you wish to participate in this run, you need to agree to have your lunar coins reset to 0. Type <color=#00cc00>\"AGREE\"</color> in the chat if you agree. Otherwise, leave the game and keep your coins. The game will resume once everyone present agrees.</size>"
			});

			foreach (var p in PlayerCharacterMasterController.instances)
			{
				p.master.GetBody().AddBuff(BuffIndex.HiddenInvincibility);
			}

			while (!AllAgree && Run.instance)
			{
				foreach (var p in PlayerCharacterMasterController.instances)
				{
					var state = p.master.GetBody().gameObject.GetComponent<SetStateOnHurt>();
					if (state.targetStateMachine.state.GetType().Equals(typeof(EntityStates.GenericCharacterMain)))
					{
						state.SetFrozen(10000f);
					}
				}
				yield return new WaitForSeconds(1f);
			}

		}

	}

	internal class SteamPlayer
	{
		public CSteamID steamID;
		public bool isReady;
		public NetworkConnection networkConnection;
		public string playerName;

		public SteamPlayer(PlayerCharacterMasterController player)
		{
			steamID = player.networkUser.id.steamId;
			isReady = false;
			networkConnection = player.master.GetComponent<NetworkIdentity>().clientAuthorityOwner;
			playerName = player.networkUser.userName;
		}
	}
}
