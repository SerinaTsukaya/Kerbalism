﻿using System;
using System.Collections.Generic;
using Experience;
using UnityEngine;
using KSP.Localization;

namespace KERBALISM
{

	public sealed class Experiment : PartModule, ISpecifics, IPartMassModifier
	{
		// config
		[KSPField] public string experiment_id;               // id of associated experiment definition
		[KSPField] public string experiment_desc = string.Empty;  // some nice lines of text
		[KSPField] public double data_rate;                   // sampling rate in Mb/s
		[KSPField] public double ec_rate;                     // EC consumption rate per-second
		[KSPField] public float sample_mass = 0f;             // if set to anything but 0, the experiment is a sample.
		[KSPField] public float sample_reservoir = 0f;        // the amount of sampling mass this unit is shipped with
		[KSPField] public bool sample_collecting = false;     // if set to true, the experiment will generate mass out of nothing
		[KSPField] public bool allow_shrouded = true;         // true if data can be transmitted

		[KSPField] public string requires = string.Empty;     // additional requirements that must be met

		[KSPField] public string crew_operate = string.Empty; // operator crew. if set, crew has to be on vessel while recording
		[KSPField] public string crew_reset = string.Empty;   // reset crew. if set, experiment will stop recording after situation change
		[KSPField] public string crew_prepare = string.Empty; // prepare crew. if set, experiment will require crew to set up before it can start recording 

		// animations
		[KSPField] public string anim_deploy = string.Empty; // deploy animation

		// persistence
		[KSPField(isPersistant = true)] public bool recording;
		[KSPField(isPersistant = true)] public string issue = string.Empty;
		[KSPField(isPersistant = true)] public string last_subject_id = string.Empty;
		[KSPField(isPersistant = true)] public bool didPrepare = false;
		[KSPField(isPersistant = true)] public double dataSampled = 0.0;
		[KSPField(isPersistant = true)] public bool shrouded = false;
		[KSPField(isPersistant = true)] public double remainingSampleMass = -1;
		[KSPField(isPersistant = true)] public bool broken = false;
		[KSPField(isPersistant = true)] public double scienceValue = 0;
		[KSPField(isPersistant = true)] public bool forcedRun = false;
		[KSPField(isPersistant = true)] public uint privateHdId = 0;


		private State state = State.STOPPED;
		// animations
		private Animator deployAnimator;

		private CrewSpecs operator_cs;
		private CrewSpecs reset_cs;
		private CrewSpecs prepare_cs;

		private String situationIssue = String.Empty;
		[KSPField(guiActive = false, guiName = "_")] private string ExperimentStatus = string.Empty;

		private enum State
		{
			STOPPED = 0, WAITING, RUNNING, ISSUE
		}

		private static State GetState(double scienceValue, string issue, bool recording, bool forcedRun)
		{
			bool hasValue = scienceValue >= 0.1;
			bool smartScience = PreferencesBasic.Instance.smartScience;

			if (issue.Length > 0) return State.ISSUE;
			if (!recording) return State.STOPPED;
			if (!hasValue && forcedRun) return State.RUNNING;
			if (!hasValue && smartScience) return State.WAITING;
			return State.RUNNING;
		}

		private State GetState()
		{
			return GetState(scienceValue, issue, recording, forcedRun);
		}

		public override void OnLoad(ConfigNode node)
		{
			// build up science sample mass database
			if (HighLogic.LoadedScene == GameScenes.LOADING)
			{
				Science.RegisterSampleMass(experiment_id, sample_mass);
			}
		}

		public override void OnStart(StartState state)
		{
			// don't break tutorial scenarios
			if (Lib.DisableScenario(this)) return;

			if(remainingSampleMass < 0) {
				remainingSampleMass = sample_mass;
				if (sample_reservoir > float.Epsilon)
					remainingSampleMass = sample_reservoir;
			}

			// create animator
			deployAnimator = new Animator(part, anim_deploy);

			// set initial animation state
			deployAnimator.Still(recording ? 1.0 : 0.0);

			// parse crew specs
			if(!string.IsNullOrEmpty(crew_operate))
				operator_cs = new CrewSpecs(crew_operate);
			if (!string.IsNullOrEmpty(crew_reset))
				reset_cs = new CrewSpecs(crew_reset);
			if (!string.IsNullOrEmpty(crew_prepare))
				prepare_cs = new CrewSpecs(crew_prepare);

			foreach(var hd in part.FindModulesImplementing<HardDrive>())
			{
				if (hd.experiment_id == experiment_id) privateHdId = part.flightID;
			}
		}

		public void Update()
		{
			var exp = Science.Experiment(experiment_id);

			// in flight
			if (Lib.IsFlight())
			{
				Vessel v = FlightGlobals.ActiveVessel;
				if (v == null || EVA.IsDead(v)) return;

				// get info from cache
				Vessel_info vi = Cache.VesselInfo(vessel);

				// do nothing if vessel is invalid
				if (!vi.is_valid) return;

				var sampleSize = exp.max_amount;
				var recordedPercent = Lib.HumanReadablePerc(dataSampled / sampleSize);
				var eta = data_rate < double.Epsilon || dataSampled >= sampleSize ? " done" : " " + Lib.HumanReadableCountdown((sampleSize - dataSampled) / data_rate);

				// update ui
				var title = Lib.Ellipsis(exp.name, Styles.ScaleStringLength(24));

				string statusString = string.Empty;
				switch (state) {
					case State.ISSUE: statusString = Lib.Color("yellow", issue); break;
					case State.RUNNING: statusString = Lib.HumanReadablePerc(dataSampled / sampleSize) + "..."; break;
					case State.WAITING: statusString = "waiting..."; break;
					case State.STOPPED: statusString = "stopped"; break;
				}

				Events["Toggle"].guiName = Lib.StatusToggle(exp.name, statusString);
				Events["Toggle"].active = (prepare_cs == null || didPrepare);

				Events["Prepare"].guiName = Lib.BuildString("Prepare <b>", exp.name, "</b>");
				Events["Prepare"].active = !didPrepare && prepare_cs != null && string.IsNullOrEmpty(last_subject_id);

				Events["Reset"].guiName = Lib.BuildString("Reset <b>", exp.name, "</b>");
				// we need a reset either if we have recorded data or did a setup
				bool resetActive = sample_mass > float.Epsilon && (reset_cs != null || prepare_cs != null) && !string.IsNullOrEmpty(last_subject_id);
				Events["Reset"].active = resetActive;

				Fields["ExperimentStatus"].guiName = exp.name;
				Fields["ExperimentStatus"].guiActive = true;

				if (issue.Length > 0)
					ExperimentStatus = Lib.BuildString("<color=#ffff00>", issue, "</color>");
				else if (dataSampled > 0)
				{
					string a = string.Empty;
					string b = string.Empty;

					if (sample_mass < float.Epsilon)
					{
						a = Lib.HumanReadableDataSize(dataSampled);
						b = Lib.HumanReadableDataSize(sampleSize);
					}
					else
					{
						a = Lib.SampleSizeToSlots(dataSampled).ToString();
						b = Lib.HumanReadableSampleSize(sampleSize);

						if (remainingSampleMass > double.Epsilon)
							b += " " + Lib.HumanReadableMass(remainingSampleMass) + " left";
					}

					ExperimentStatus = Lib.BuildString(a, "/", b, eta);
				}
				else
				{
					var size = sample_mass < double.Epsilon ? Lib.HumanReadableDataSize(sampleSize) : Lib.HumanReadableSampleSize(sampleSize);
					ExperimentStatus = Lib.BuildString("ready ", size, " in ", Lib.HumanReadableDuration(sampleSize / data_rate));
				}

				if (scienceValue > 0.1) ExperimentStatus += " •<b>" + scienceValue.ToString("F1") + "</b>";
			}
			// in the editor
			else if (Lib.IsEditor())
			{
				// update ui
				Events["Toggle"].guiName = Lib.StatusToggle(exp.name, recording ? "recording" : "stopped");
				Events["Reset"].active = false;
				Events["Prepare"].active = false;
			}
		}

		public void FixedUpdate()
		{
			// basic sanity checks
			if (Lib.IsEditor()) return;
			if (!Cache.VesselInfo(vessel).is_valid) return;

			// get ec handler
			Resource_info ec = ResourceCache.Info(vessel, "ElectricCharge");
			shrouded = part.ShieldedFromAirstream;
			issue = TestForIssues(vessel, ec, this, broken,
				remainingSampleMass, didPrepare, shrouded, last_subject_id);

			if (!string.IsNullOrEmpty(issue))
				return;

			var subject_id = Science.Generate_subject_id(experiment_id, vessel);
			if (last_subject_id != subject_id)
			{
				dataSampled = 0;
				forcedRun = false;
			}
			last_subject_id = subject_id;
			scienceValue = Science.Value(last_subject_id);
			state = GetState();

			if (state != State.RUNNING)
				return;

			var exp = Science.Experiment(experiment_id);
			if (dataSampled >= exp.max_amount)
				return;

			// if experiment is active and there are no issues
			DoRecord(ec, subject_id);
		}

		private void DoRecord(Resource_info ec, string subject_id)
		{
			var stored = DoRecord(this, subject_id, vessel, ec, privateHdId, remainingSampleMass, dataSampled, out dataSampled, out remainingSampleMass);
			if (!stored) issue = "insufficient storage";
		}

		private static bool DoRecord(Experiment experiment, string subject_id, Vessel vessel, Resource_info ec, uint hdId,
		                             double remainingSampleMass, double dataSampled,
		                             out double sampledOut, out double remainingSampleMassOut)
		{
			var exp = Science.Experiment(subject_id);
			double elapsed = Kerbalism.elapsed_s;
			double chunkSize = Math.Min(experiment.data_rate * elapsed, exp.max_amount);
			double massDelta = experiment.sample_mass * chunkSize / exp.max_amount;

			bool isFile = experiment.sample_mass < float.Epsilon;
			Drive drive = null;
			var vd = DB.Vessel(vessel);
			if (hdId != 0 && vd.drives.ContainsKey(hdId)) drive = vd.drives[hdId];
			else drive = isFile ? DB.Vessel(vessel).FileDrive(chunkSize) : DB.Vessel(vessel).SampleDrive(chunkSize, subject_id);

			// on high time warp this chunk size could be too big, but we could store a sizable amount if we process less
			double maxCapacity = isFile ? drive.FileCapacityAvailable() : drive.SampleCapacityAvailable(subject_id);
			if (maxCapacity < chunkSize)
			{
				double factor = maxCapacity / chunkSize;
				chunkSize *= factor;
				massDelta *= factor;
				elapsed *= factor;
			}

			bool stored = false;
			if (isFile)
				stored = drive.Record_file(subject_id, chunkSize, true, true);
			else
				stored = drive.Record_sample(subject_id, chunkSize, massDelta);

			if (stored)
			{
				// consume ec
				ec.Consume(experiment.ec_rate * elapsed);
				dataSampled += chunkSize;
				dataSampled = Math.Min(dataSampled, exp.max_amount);
				sampledOut = dataSampled;
				if (!experiment.sample_collecting)
				{
					remainingSampleMass -= massDelta;
					remainingSampleMass = Math.Max(remainingSampleMass, 0);
				}
				remainingSampleMassOut = remainingSampleMass;
				return true;
			}

			sampledOut = dataSampled;
			remainingSampleMassOut = remainingSampleMass;
			return false;
		}

		public static void BackgroundUpdate(Vessel v, ProtoPartModuleSnapshot m, Experiment experiment, Resource_info ec, double elapsed_s)
		{
			bool didPrepare = Lib.Proto.GetBool(m, "didPrepare", false);
			bool shrouded = Lib.Proto.GetBool(m, "shrouded", false);
			string last_subject_id = Lib.Proto.GetString(m, "last_subject_id", "");
			double remainingSampleMass = Lib.Proto.GetDouble(m, "remainingSampleMass", 0);
			bool broken = Lib.Proto.GetBool(m, "broken", false);
			bool forcedRun = Lib.Proto.GetBool(m, "forcedRun", false);
			bool recording = Lib.Proto.GetBool(m, "recording", false);

			string issue = TestForIssues(v, ec, experiment, broken,
				remainingSampleMass, didPrepare, shrouded, last_subject_id);
			Lib.Proto.Set(m, "issue", issue);

			if (!string.IsNullOrEmpty(issue))
				return;

			var subject_id = Science.Generate_subject_id(experiment.experiment_id, v);
			Lib.Proto.Set(m, "last_subject_id", subject_id);

			double dataSampled = Lib.Proto.GetDouble(m, "dataSampled");

			if (last_subject_id != subject_id)
			{
				dataSampled = 0;
				Lib.Proto.Set(m, "forcedRun", false);
			}

			double scienceValue = Science.Value(last_subject_id);
			Lib.Proto.Set(m, "scienceValue", scienceValue);

			var state = GetState(scienceValue, issue, recording, forcedRun);
			if (state != State.RUNNING)
				return;
			if (dataSampled >= Science.Experiment(subject_id).max_amount)
				return;

			uint privateHdId = Lib.Proto.GetUInt(m, "privateHdId", 0);
			var stored = DoRecord(experiment, subject_id, v, ec, privateHdId, remainingSampleMass, dataSampled, out dataSampled, out remainingSampleMass);
			if (!stored) Lib.Proto.Set(m, "issue", "insufficient storage");

			Lib.Proto.Set(m, "dataSampled", dataSampled);
			Lib.Proto.Set(m, "remainingSampleMass", remainingSampleMass);
		}

		public void ReliablityEvent(bool breakdown)
		{
			broken = breakdown;
		}

		private static string TestForIssues(Vessel v, Resource_info ec, Experiment experiment, bool broken,
			double remainingSampleMass, bool didPrepare, bool isShrouded, string last_subject_id)
		{
			var subject_id = Science.Generate_subject_id(experiment.experiment_id, v);

			if (broken)
				return "broken";

			if (isShrouded && !experiment.allow_shrouded)
				return "shrouded";
			
			bool needsReset = experiment.crew_reset.Length > 0
				&& !string.IsNullOrEmpty(last_subject_id) && subject_id != last_subject_id;
			if (needsReset) return "reset required";

			if (ec.amount < double.Epsilon && experiment.ec_rate > double.Epsilon)
				return "no Electricity";
			
			if (!string.IsNullOrEmpty(experiment.crew_operate))
			{
				var cs = new CrewSpecs(experiment.crew_operate);
				if (!cs.Check(v)) return cs.Warning();
			}

			if (!experiment.sample_collecting && remainingSampleMass < double.Epsilon && experiment.sample_mass > double.Epsilon)
				return "depleted";

			if (!didPrepare && !string.IsNullOrEmpty(experiment.crew_prepare))
				return "not prepared";

			string situationIssue = Science.TestRequirements(experiment.experiment_id, experiment.requires, v);
			if (situationIssue.Length > 0)
				return Science.RequirementText(situationIssue);

			var isFile = experiment.sample_mass < double.Epsilon;
			var drive = isFile ? DB.Vessel(v).FileDrive() : DB.Vessel(v).SampleDrive(0, subject_id);
			var experimentSize = Science.Experiment(subject_id).max_amount;
			double available = experiment.sample_mass < float.Epsilon ? drive.FileCapacityAvailable() : drive.SampleCapacityAvailable();
			if (Math.Min(experiment.data_rate * Kerbalism.elapsed_s, experimentSize) > available)
				return "insufficient storage";

			return string.Empty;
		}

		[KSPEvent(guiActiveUnfocused = true, guiName = "_", active = false)]
		public void Prepare()
		{
			// disable for dead eva kerbals
			Vessel v = FlightGlobals.ActiveVessel;
			if (v == null || EVA.IsDead(v))
				return;

			if (prepare_cs == null)
				return;

			// check trait
			if (!prepare_cs.Check(v))
			{
				Message.Post(
				  Lib.TextVariant
				  (
					"I'm not qualified for this",
					"I will not even know where to start",
					"I'm afraid I can't do that"
				  ),
				  reset_cs.Warning()
				);
			}

			didPrepare = true;

			Message.Post(
			  "Preparation Complete",
			  Lib.TextVariant
			  (
				"Ready to go",
				"Let's start doing some science!"
			  )
			);
		}

		[KSPEvent(guiActiveUnfocused = true, guiName = "_", active = false)]
		public void Reset()
		{
			Reset(true);
		}

		public bool Reset(bool showMessage)
		{
			// disable for dead eva kerbals
			Vessel v = FlightGlobals.ActiveVessel;
			if (v == null || EVA.IsDead(v))
				return false;

			if (reset_cs == null)
				return false;

			// check trait
			if (!reset_cs.Check(v))
			{
				if(showMessage)
				{
					Message.Post(
					  Lib.TextVariant
					  (
						"I'm not qualified for this",
						"I will not even know where to start",
						"I'm afraid I can't do that"
					  ),
					  reset_cs.Warning()
					);
				}
				return false;
			}

			last_subject_id = string.Empty;
			didPrepare = false;

			if(showMessage)
			{
				Message.Post(
				  "Reset Done",
				  Lib.TextVariant
				  (
					"It's good to go again",
					"Ready for the next bit of science"
				  )
				);
			}
			return true; 
		}

		private bool IsExperimentRunningOnVessel()
		{
			foreach(var e in vessel.FindPartModulesImplementing<Experiment>())
			{
				if (e.experiment_id == experiment_id && e.recording) return true;
			}
			return false;
		}

		public static void PostMultipleRunsMessage(string title)
		{
			Message.Post(Lib.Color("red", "ALREADY RUNNING", true), "Can't start " + title + " a second time on the same vessel");
		}

		[KSPEvent(guiActiveUnfocused = true, guiActive = true, guiActiveEditor = true, guiName = "_", active = true)]
		public void Toggle()
		{
			if(Lib.IsEditor())
			{
				recording = !recording;
				deployAnimator.Play(!recording, false);
				GameEvents.onEditorShipModified.Fire(EditorLogic.fetch.ship);
				return;
			}

			if (state == State.WAITING)
			{
				forcedRun = true;
				recording = true;
				return;
			}

			// The same experiment must run only once on a vessel
			if (!recording)
			{
				recording = !IsExperimentRunningOnVessel();
				if(!recording) PostMultipleRunsMessage(Science.Experiment(experiment_id).name);
			}
			else
				recording = false;

			if (!recording)
			{
				dataSampled = 0;
				forcedRun = false;
			}

			// play deploy animation if exist
			deployAnimator.Play(!recording, false);
		}

		// action groups
		[KSPAction("Start")] public void Start(KSPActionParam param)
		{
			switch (GetState()) {
				case State.STOPPED:
				case State.WAITING:
					Toggle();
					break;
			}
		}
		[KSPAction("Stop")] public void Stop(KSPActionParam param) {
			if(recording) Toggle();
		}

		// part tooltip
		public override string GetInfo()
		{
			return Specs().Info();
		}

		// specifics support
		public Specifics Specs()
		{
			var specs = new Specifics();
			var exp = Science.Experiment(experiment_id);
			if (exp == null)
			{
				specs.Add(Localizer.Format("#KERBALISM_ExperimentInfo_Unknown"));
				return specs;
			}

			specs.Add(Lib.BuildString("<b>", exp.name, "</b>"));
			if(!string.IsNullOrEmpty(experiment_desc))
			{
				specs.Add(Lib.BuildString("<i>", experiment_desc, "</i>"));
			}
			
			specs.Add(string.Empty);
			double expSize = exp.max_amount;
			if (sample_mass < float.Epsilon)
			{
				specs.Add("Data", Lib.HumanReadableDataSize(expSize));
				specs.Add("Data rate", Lib.HumanReadableDataRate(data_rate));
				specs.Add("Duration", Lib.HumanReadableDuration(expSize / data_rate));
			}
			else
			{
				specs.Add("Sample size", Lib.HumanReadableSampleSize(expSize));
				specs.Add("Sample mass", Lib.HumanReadableMass(sample_mass));
				specs.Add("Duration", Lib.HumanReadableDuration(expSize / data_rate));
			}

			specs.Add("EC required", Lib.HumanReadableRate(ec_rate));
			if (crew_operate.Length > 0) specs.Add("Opration", new CrewSpecs(crew_operate).Info());
			if (crew_reset.Length > 0) specs.Add("Reset", new CrewSpecs(crew_reset).Info());
			if (crew_prepare.Length > 0) specs.Add("Preparation", new CrewSpecs(crew_prepare).Info());

			if(!string.IsNullOrEmpty(requires))
			{
				specs.Add(string.Empty);
				specs.Add("<color=#00ffff>Requirements:</color>", string.Empty);
				var tokens = Lib.Tokenize(requires, ',');
				foreach (string s in tokens) specs.Add(Lib.BuildString("• <b>", Science.RequirementText(s), "</b>"));
			}
			return specs;
		}

		// module mass support
		public float GetModuleMass(float defaultMass, ModifierStagingSituation sit) { return sample_collecting ? 0 : (float)remainingSampleMass; }
		public ModifierChangeWhen GetModuleMassChangeWhen() { return ModifierChangeWhen.CONSTANTLY; }
	}
} // KERBALISM

