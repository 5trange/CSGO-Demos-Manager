﻿using DemoInfo.DP;
using DemoInfo.DT;
using DemoInfo.Messages;
using DemoInfo.ST;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics;

namespace DemoInfo
{
	public class DemoParser
	{
		#region Events

		/// <summary>
		/// Called once when the Header of the demo is parsed
		/// </summary>
		public event EventHandler<HeaderParsedEventArgs> HeaderParsed;

		public event EventHandler<MatchStartedEventArgs> MatchStarted;

		public event EventHandler<RoundStartedEventArgs> RoundStart;

		public event EventHandler<TickDoneEventArgs> TickDone;

		public event EventHandler<PlayerKilledEventArgs> PlayerKilled;

		public event EventHandler<WeaponFiredEventArgs> WeaponFired;

		public event EventHandler<SmokeEventArgs> SmokeNadeStarted;
		public event EventHandler<SmokeEventArgs> SmokeNadeEnded;

		public event EventHandler<DecoyEventArgs> DecoyNadeStarted;
		public event EventHandler<DecoyEventArgs> DecoyNadeEnded;

		public event EventHandler<FireEventArgs> FireNadeStarted;
		public event EventHandler<FireEventArgs> FireNadeEnded;

		public event EventHandler<FlashEventArgs> FlashNadeExploded;
		public event EventHandler<GrenadeEventArgs> ExplosiveNadeExploded;

		public event EventHandler<NadeEventArgs> NadeReachedTarget;

		public event EventHandler<BombEventArgs> BombBeginPlant;
		public event EventHandler<BombEventArgs> BombAbortPlant;
		public event EventHandler<BombEventArgs> BombPlanted;
		public event EventHandler<BombEventArgs> BombDefused;
		public event EventHandler<BombEventArgs> BombExploded;
		public event EventHandler<BombDefuseEventArgs> BombBeginDefuse;
		public event EventHandler<BombDefuseEventArgs> BombAbortDefuse;

		#endregion

		#region Information

		public string Map {
			get { return Header.MapName; }
		}

		#endregion

		BinaryReader reader;

		public DemoHeader Header { get; private set; }

		internal DataTableParser DataTables = new DataTableParser();
		StringTableParser StringTables = new StringTableParser();

		internal Dictionary<ServerClass, EquipmentElement> equipmentMapping = new Dictionary<ServerClass, EquipmentElement>();

		public Dictionary<int, Player> Players = new Dictionary<int, Player>();

		public Player[] PlayerInformations = new Player[64];

		public PlayerInfo[] RawPlayers = new PlayerInfo[64];

		const int MAX_EDICT_BITS = 11;
		internal const int INDEX_MASK = ( ( 1 << MAX_EDICT_BITS ) - 1 );
		internal const int MAX_ENTITIES = ( ( 1 << MAX_EDICT_BITS ) );

		internal Entity[] Entities = new Entity[MAX_ENTITIES]; //Max 2048 entities. 

		public List<CSVCMsg_CreateStringTable> stringTables = new List<CSVCMsg_CreateStringTable>();

		/// <summary>
		/// Gets or sets a value indicating whether this <see cref="DemoInfo.DemoParser"/> will attribute weapons to the players.
		/// Default: false
		/// Note: Enableing this might decrease performance in the current implementation. Will be removed once the code is optimized. 
		/// </summary>
		/// <value><c>true</c> to attribute weapons; otherwise, <c>false</c>.</value>
		public bool ShallAttributeWeapons { get; set; }


		internal int bombSiteAEntityIndex = -1;
		internal int bombSiteBEntityIndex = -1;

		/// <summary>
		/// The ID of the CT-Team
		/// </summary>
		int ctID = -1;
		/// <summary>
		/// The ID of the terrorist team
		/// </summary>
		int tID = -1;

		public int CTScore  {
			get;
			private set;
		}

		public int TScore  {
			get;
			private set;
		}

		#region Context for GameEventHandler

		internal Dictionary<int, CSVCMsg_GameEventList.descriptor_t> GEH_Descriptors = null;
		internal List<Player> GEH_BlindPlayers = new List<Player>();

		#endregion

		internal Dictionary<int, byte[]> instanceBaseline = new Dictionary<int, byte[]>();

		public float TickRate {
			get { return this.Header.PlaybackFrames / this.Header.PlaybackTime; }
		}

		public float TickTime {
			get { return this.Header.PlaybackTime / this.Header.PlaybackFrames; }
   		}

		public int CurrentTick { get; private set; }

		public float CurrentTime { get { return CurrentTick * TickTime; } }


		public DemoParser(Stream input)
		{
			reader = new BinaryReader(input);
		}

		public void ParseDemo(bool fullParse)
		{
			ParseHeader();

			if (HeaderParsed != null)
				HeaderParsed(this, new HeaderParsedEventArgs(Header));

			if (fullParse) {
				while (ParseNextTick()) {
				}
			}
		}

		public bool ParseNextTick()
		{
			bool b = ParseTick();
			
			for (int i = 0; i < RawPlayers.Length; i++) {
				if (RawPlayers[i] == null)
					continue;

				var rawPlayer = RawPlayers[i];

				int id = rawPlayer.UserID;

				if (PlayerInformations[i] != null) { //There is an good entity for this
					if (!Players.ContainsKey(id))
						Players[id] = PlayerInformations[i];

					Player p = Players[id];
					p.Name = rawPlayer.Name;
					p.SteamID = rawPlayer.XUID;

					if (p.IsAlive) {
						p.LastAlivePosition = p.Position.Copy();
					}
				}
			}

			if(ShallAttributeWeapons)
				AttributeWeapons();

			if (b) {
				if (TickDone != null)
					TickDone(this, new TickDoneEventArgs());
			}

			return b;
		}

		private void ParseHeader()
		{
			var header = DemoHeader.ParseFrom(reader);

			if (header.Filestamp != "HL2DEMO")
				throw new Exception("Invalid File-Type - expecting HL2DEMO");

			if (header.Protocol != 4)
				throw new Exception("Invalid Demo-Protocol");

			Header = header;
		}

		private bool ParseTick()
		{
			DemoCommand command = (DemoCommand)reader.ReadByte();

			reader.ReadInt32(); // tick number
			reader.ReadByte(); // player slot

			this.CurrentTick++; // = TickNum;

			switch (command) {
			case DemoCommand.Synctick:
				break;
			case DemoCommand.Stop:
				return false;
			case DemoCommand.ConsoleCommand:
				using (var volvo = reader.ReadVolvoPacket())
					;
				break;
			case DemoCommand.DataTables:
				using (var volvo = reader.ReadVolvoPacket())
					DataTables.ParsePacket(volvo);

				for (int i = 0; i < DataTables.ServerClasses.Count; i++) {
					var sc = DataTables.ServerClasses[i];
					if (sc.DTName.StartsWith("DT_Weapon")) {
						var s = sc.DTName.Substring(9).ToLower();
						equipmentMapping.Add(sc, Equipment.MapEquipment(s));
					} else if (sc.DTName == "DT_Flashbang") {
						equipmentMapping.Add(sc, EquipmentElement.Flash);
					} else if (sc.DTName == "DT_SmokeGrenade") {
						equipmentMapping.Add(sc, EquipmentElement.Smoke);
					} else if (sc.DTName == "DT_HEGrenade") {
						equipmentMapping.Add(sc, EquipmentElement.HE);
					} else if (sc.DTName == "DT_DecoyGrenade") {
						equipmentMapping.Add(sc, EquipmentElement.Decoy);
					} else if (sc.DTName == "DT_IncendiaryGrenade") {
						equipmentMapping.Add(sc, EquipmentElement.Incendiary);
					} else if (sc.DTName == "DT_MolotovGrenade") {
						equipmentMapping.Add(sc, EquipmentElement.Molotov);
					}
				}

				BindEntites();

				break;
			case DemoCommand.StringTables:
				using (var volvo = reader.ReadVolvoPacket())
					StringTables.ParsePacket(volvo, this);
				break;
			case DemoCommand.UserCommand:
				reader.ReadInt32();
				using (var volvo = reader.ReadVolvoPacket())
					;
				break;
			case DemoCommand.Signon:
			case DemoCommand.Packet:
				ParseDemoPacket();
				break;
			default:
				throw new Exception("Can't handle Demo-Command " + command);
			}

			return true;
		}

		private void ParseDemoPacket()
		{
			CommandInfo.Parse(reader);
			reader.ReadInt32(); // SeqNrIn
			reader.ReadInt32(); // SeqNrOut

			using (var volvo = reader.ReadVolvoPacket())
				DemoPacketParser.ParsePacket(volvo, this);
   		}

		private void AttributeWeapons()
		{

		}

		private void BindEntites()
		{
			//Okay, first the team-stuff. 
			HandleTeamScores();

			HandlePlayers();

		}

		private void HandleTeamScores()
		{
			DataTables.FindByName("CCSTeam")
				.OnNewEntity += (object sender, EntityCreatedEventArgs e) => {

				string team = null;
				int teamID = -1;
				int score = 0;

				e.Entity.FindProperty("m_scoreTotal").IntRecived += (xx, update) => { 
					score = update.Value;
				};

				e.Entity.FindProperty("m_iTeamNum").IntRecived += (xx, update) => { 
					teamID = update.Value;

					if(team == "CT")
					{
						this.ctID = teamID;
						CTScore = score;
						foreach(var p in PlayerInformations.Where(a => a != null && a.TeamID == teamID))
							p.Team = Team.CounterTerrorist;
					}

					if(team == "TERRORIST")
					{
						this.tID = teamID;
						TScore = score;
						foreach(var p in PlayerInformations.Where(a => a != null && a.TeamID == teamID))
							p.Team = Team.CounterTerrorist;
					}
				};

				e.Entity.FindProperty("m_szTeamname").StringRecived += (sender_, teamName) => { 
					team = teamName.Value;

					//We got the name. Lets bind the updates accordingly!
					if(teamName.Value == "CT")
					{
						CTScore = score;
						e.Entity.FindProperty("m_scoreTotal").IntRecived += (xx, update) => { 
							CTScore = update.Value;
						};

						if(teamID != -1)
						{
							this.ctID = teamID;
							foreach(var p in PlayerInformations.Where(a => a != null && a.TeamID == teamID))
								p.Team = Team.CounterTerrorist;
						}

					}
					else if(teamName.Value == "TERRORIST")
					{
						e.Entity.FindProperty("m_scoreTotal").IntRecived += (xx, update) => TScore = update.Value;
						e.Entity.FindProperty("m_iTeamNum").IntRecived += (xx, update) => tID = update.Value;


						if(teamID != -1)
						{
							this.tID = teamID;
							foreach(var p in PlayerInformations.Where(a => a != null && a.TeamID == teamID))
								p.Team = Team.Terrorist;
						}
					}
				};
			};
		}

		private void HandlePlayers()
		{
			DataTables.FindByName("CCSPlayer").OnNewEntity += (object sender, EntityCreatedEventArgs e) => 
			{
				HandleNewPlayer(e.Entity);
			};
		}

		private void HandleNewPlayer(Entity playerEntity)
		{
			Player p = new Player();
			this.PlayerInformations[playerEntity.ID - 1] = p;

			p.Name = "unconnected";
			p.EntityID = playerEntity.ID;
			p.SteamID = -1;
			p.Entity = playerEntity;
			p.Position = new Vector();
			p.Velocity = new Vector();

			//position update
			playerEntity.FindProperty("cslocaldata.m_vecOrigin").VectorRecived += (sender, e) => {
				p.Position.X = e.Value.X; 
				p.Position.Y = e.Value.Y;
			};

			playerEntity.FindProperty("cslocaldata.m_vecOrigin[2]").FloatRecived += (sender, e) => {
				p.Position.Z = e.Value; 
			};

			//team update
			//problem: Teams are networked after the players... How do we solve that?
			playerEntity.FindProperty("m_iTeamNum").IntRecived += (sender, e) => { 

				p.TeamID = e.Value;

				if (e.Value == ctID)
					p.Team = Team.CounterTerrorist;
				else if (e.Value == tID)
					p.Team = Team.Terrorist;
				else
					p.Team = Team.Spectate;
			};

			//update some stats
			playerEntity.FindProperty("m_iHealth").IntRecived += (sender, e) => p.HP = e.Value;
			playerEntity.FindProperty("m_ArmorValue").IntRecived += (sender, e) => p.Armor = e.Value;
			playerEntity.FindProperty("m_bHasDefuser").IntRecived += (sender, e) => p.HasDefuseKit = e.Value == 1;
			playerEntity.FindProperty("m_bHasHelmet").IntRecived += (sender, e) => p.HasHelmet = e.Value == 1;
			playerEntity.FindProperty("m_iAccount").IntRecived += (sender, e) => p.Money = e.Value;
			playerEntity.FindProperty("m_angEyeAngles[1]").FloatRecived += (sender, e) => p.ViewDirectionX = e.Value;
			playerEntity.FindProperty("m_angEyeAngles[0]").FloatRecived += (sender, e) => p.ViewDirectionY = e.Value;


			playerEntity.FindProperty("localdata.m_vecVelocity[0]").FloatRecived += (sender, e) => p.Velocity.X = e.Value;
			playerEntity.FindProperty("localdata.m_vecVelocity[1]").FloatRecived += (sender, e) => p.Velocity.Y = e.Value;
			playerEntity.FindProperty("localdata.m_vecVelocity[2]").FloatRecived += (sender, e) => p.Velocity.Z = e.Value;
		}

		#region EventCaller

		internal void RaiseMatchStarted()
		{
			if (MatchStarted != null)
				MatchStarted(this, new MatchStartedEventArgs());
		}

		public void RaiseRoundStart()
		{
			if (RoundStart != null)
				RoundStart(this, new RoundStartedEventArgs());

		}

		internal void RaisePlayerKilled(PlayerKilledEventArgs kill)
		{
			if (PlayerKilled != null)
				PlayerKilled(this, kill);
		}

		internal void RaiseWeaponFired(WeaponFiredEventArgs fire)
		{
			if (WeaponFired != null)
				WeaponFired(this, fire);
		}


		internal void RaiseSmokeStart(SmokeEventArgs args)
		{
			if (SmokeNadeStarted != null)
				SmokeNadeStarted(this, args);

			if (NadeReachedTarget != null)
				NadeReachedTarget(this, args);
		}

		internal void RaiseSmokeEnd(SmokeEventArgs args)
		{
			if (SmokeNadeEnded != null)
				SmokeNadeEnded(this, args);

			if (NadeReachedTarget != null)
				NadeReachedTarget(this, args);
		}

		internal void RaiseDecoyStart(DecoyEventArgs args)
		{
			if (DecoyNadeStarted != null)
				DecoyNadeStarted(this, args);

			if (NadeReachedTarget != null)
				NadeReachedTarget(this, args);
		}

		internal void RaiseDecoyEnd(DecoyEventArgs args)
		{
			if (DecoyNadeEnded != null)
				DecoyNadeEnded(this, args);

			if (NadeReachedTarget != null)
				NadeReachedTarget(this, args);
		}

		internal void RaiseFireStart(FireEventArgs args)
		{
			if (FireNadeStarted != null)
				FireNadeStarted(this, args);

			if (NadeReachedTarget != null)
				NadeReachedTarget(this, args);
		}

		internal void RaiseFireEnd(FireEventArgs args)
		{
			if (FireNadeEnded != null)
				FireNadeEnded(this, args);

			if (NadeReachedTarget != null)
				NadeReachedTarget(this, args);
		}

		internal void RaiseFlashExploded(FlashEventArgs args)
		{
			if (FlashNadeExploded != null)
				FlashNadeExploded(this, args);

			if (NadeReachedTarget != null)
				NadeReachedTarget(this, args);
		}

		internal void RaiseGrenadeExploded(GrenadeEventArgs args)
		{
			if (ExplosiveNadeExploded != null)
				ExplosiveNadeExploded(this, args);

			if (NadeReachedTarget != null)
				NadeReachedTarget(this, args);
		}

		internal void RaiseBombBeginPlant(BombEventArgs args)
		{
			if (BombBeginPlant != null)
				BombBeginPlant(this, args);
		}

		internal void RaiseBombAbortPlant(BombEventArgs args)
		{
			if (BombAbortPlant != null)
				BombAbortPlant(this, args);
		}

		internal void RaiseBombPlanted(BombEventArgs args)
		{
			if (BombPlanted != null)
				BombPlanted(this, args);
		}

		internal void RaiseBombDefused(BombEventArgs args)
		{
			if (BombDefused != null)
				BombDefused(this, args);
		}

		internal void RaiseBombExploded(BombEventArgs args)
		{
			if (BombExploded != null)
				BombExploded(this, args);
		}

		internal void RaiseBombBeginDefuse(BombDefuseEventArgs args)
		{
			if (BombBeginDefuse != null)
				BombBeginDefuse(this, args);
		}

		internal void RaiseBombAbortDefuse(BombDefuseEventArgs args)
		{
			if (BombAbortDefuse != null)
				BombAbortDefuse(this, args);
		}

		#endregion
	}
}
