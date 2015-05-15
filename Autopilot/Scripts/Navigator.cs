﻿#define LOG_ENABLED //remove on build

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using Sandbox.Common;
using Sandbox.Common.ObjectBuilders;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces;
using Ingame = Sandbox.ModAPI.Ingame;

using VRage.Library.Utils;
using VRageMath;

using Rynchodon.Autopilot.Instruction;
using Rynchodon.AntennaRelay;
using Rynchodon.Autopilot.Harvest;

namespace Rynchodon.Autopilot
{
	public class Navigator
	{
		private Logger myLogger = null;
		[System.Diagnostics.Conditional("LOG_ENABLED")]
		private void log(string toLog, string method = null, Logger.severity level = Logger.severity.DEBUG)
		{ alwaysLog(level, method, toLog); }
		private void alwaysLog(string toLog, string method = null, Logger.severity level = Logger.severity.WARNING)
		{ alwaysLog(level, method, toLog); }
		private void alwaysLog(Logger.severity level, string method, string toLog)
		{
			try
			{ myLogger.log(level, method, toLog, CNS.moveState.ToString() + ":" + CNS.rotateState.ToString(), CNS.landingState.ToString()); }
			catch (Exception) { }
		}

		public Sandbox.ModAPI.IMyCubeGrid myGrid { get; private set; }

		private List<Sandbox.ModAPI.IMySlimBlock> remoteControlBlocks;

		/// <summary>
		/// overrids fetching commands from display name when true
		/// </summary>
		public bool AIOverride = false;

		/// <summary>
		/// current navigation settings
		/// </summary>
		internal NavSettings CNS;
		private Pathfinder.Pathfinder myPathfinder;
		internal Pathfinder.PathfinderOutput myPathfinder_Output { get; private set; }
		//internal GridDimensions myGridDim;
		internal ThrustProfiler currentThrust;
		internal Targeter myTargeter;
		private Rotator myRotator;
		internal HarvesterAsteroid myHarvester { get; private set; }

		private IMyControllableEntity currentRemoteControl_Value;
		/// <summary>
		/// Primary remote control value.
		/// </summary>
		public IMyControllableEntity currentRCcontrol
		{
			get { return currentRemoteControl_Value; }
			set
			{
				if (currentRemoteControl_Value == value)
					return;

				if (currentRemoteControl_Value != null)
				{
					// actions on old RC
					fullStop("unsetting RC");
					reportState(ReportableState.Off, true);
				}

				currentRemoteControl_Value = value;
				myLand = null;
				if (currentRemoteControl_Value == null)
				{
					myPathfinder = null;
					CNS = new NavSettings(null);
				}
				else
				{
					myPathfinder = new Pathfinder.Pathfinder(myGrid);
					CNS = new NavSettings(this);
					myLogger.debugLog("have a new RC: " + currentRCblock.getNameOnly(), "set_currentRCcontrol()");

					// actions on new RC
					fullStop("new RC");
					reportState(ReportableState.Off, true);
				}

				myRotator = new Rotator(this);
				myHarvester = new HarvesterAsteroid(this);
			}
		}
		/// <summary>
		/// Secondary remote control value.
		/// </summary>
		public Sandbox.ModAPI.IMyCubeBlock currentRCblock
		{
			get { return currentRemoteControl_Value as Sandbox.ModAPI.IMyCubeBlock; }
			set { currentRCcontrol = value as IMyControllableEntity; }
		}
		/// <summary>
		/// Secondary remote control value.
		/// </summary>
		public IMyTerminalBlock currentRCterminal
		{ get { return currentRemoteControl_Value as IMyTerminalBlock; } }

		/// <summary>
		/// only use for position or distance, for rotation it is simpler to only use RC directions
		/// </summary>
		/// <returns></returns>
		public Sandbox.ModAPI.IMyCubeBlock getNavigationBlock()
		{
			if (CNS.landingState == NavSettings.LANDING.OFF || CNS.landLocalBlock == null)
			{
				if (myHarvester.NavigationDrill != null)
					return myHarvester.NavigationDrill;
				return currentRCblock;
			}
			else
			{
				//log("using "+CNS.landLocalBlock.DisplayNameText+" as navigation block");
				if (CNS.landingSeparateBlock != null)
					return CNS.landingSeparateBlock;
				return CNS.landLocalBlock;
			}
		}

		internal Navigator(Sandbox.ModAPI.IMyCubeGrid grid)
		{
			myGrid = grid;
			myLogger = new Logger("Navigator", () => myGrid.DisplayName, () => { return CNS.moveState.ToString() + ':' + CNS.rotateState.ToString(); }, () => CNS.landingState.ToString());
		}

		private bool needToInit = true;

		private void init()
		{
			//	find remote control blocks
			remoteControlBlocks = new List<Sandbox.ModAPI.IMySlimBlock>();
			myGrid.GetBlocks(remoteControlBlocks, block => block.FatBlock != null && block.FatBlock.BlockDefinition.TypeId == remoteControlType);

			// register for events
			myGrid.OnBlockAdded += OnBlockAdded;
			myGrid.OnBlockRemoved += OnBlockRemoved;
			myGrid.OnClose += OnClose;

			currentThrust = new ThrustProfiler(myGrid);
			CNS = new NavSettings(null);
			myTargeter = new Targeter(this);
			myInterpreter = new Interpreter(this);
			needToInit = false;
		}

		internal void Close()
		{
			myLogger.debugLog("entered Close()", "Close()()");
			if (myGrid != null)
			{
				myGrid.OnClose -= OnClose;
				myGrid.OnBlockAdded -= OnBlockAdded;
				myGrid.OnBlockRemoved -= OnBlockRemoved;
			}
			currentRCcontrol = null;
		}

		private void OnClose()
		{
			Close();
			Core.remove(this);
		}

		private void OnClose(IMyEntity closing)
		{ try { OnClose(); } catch { } }

		private static MyObjectBuilderType remoteControlType = typeof(MyObjectBuilder_RemoteControl);

		private void OnBlockAdded(Sandbox.ModAPI.IMySlimBlock addedBlock)
		{
			if (addedBlock.FatBlock != null && addedBlock.FatBlock.BlockDefinition.TypeId == remoteControlType)
				remoteControlBlocks.Add(addedBlock);
		}

		private void OnBlockRemoved(Sandbox.ModAPI.IMySlimBlock removedBlock)
		{
			if (removedBlock.FatBlock != null)
			{
				if (removedBlock.FatBlock.BlockDefinition.TypeId == remoteControlType)
					remoteControlBlocks.Remove(removedBlock);
			}
		}

		private long updateCount = 0;
		private bool pass_gridCanNavigate = true;

		/// <summary>
		/// Causes the ship to fly around, following commands.
		/// </summary>
		/// <remarks>
		/// Calling more often means more precise movements, calling too often (~ every update) will break functionality.
		/// </remarks>
		public void update()
		{
			updateCount++;
			reportState();
			if (gridCanNavigate())
			{
				// when regaining the ability to navigate, reset
				if (!pass_gridCanNavigate)
				{
					reset("cannot navigate");
					pass_gridCanNavigate = true;
				}
			}
			else
			{
				pass_gridCanNavigate = false;
				return;
			}
			if (needToInit)
				init();
			if (CNS.lockOnTarget != NavSettings.TARGET.OFF)
				myTargeter.tryLockOn();
			if (CNS.waitUntilNoCheck.CompareTo(DateTime.UtcNow) > 0)
				return;
			if (CNS.waitUntil.CompareTo(DateTime.UtcNow) > 0 || CNS.EXIT)
			{
				if (!remoteControlIsReady(currentRCblock)) // if something changes, stop waiting!
					reset("wait interrupted");
				return;
			}

			if (CNS.getTypeOfWayDest() != NavSettings.TypeOfWayDest.NULL)
				navigate();
			else // no waypoints
			{
				if (currentRCcontrol != null && myInterpreter.hasInstructions())
				{
					while (myInterpreter.hasInstructions())
					{
						myLogger.debugLog("invoking instruction: " + myInterpreter.getCurrentInstructionString(), "update()");
						Action instruction = myInterpreter.instructionQueue.Dequeue();
						try { instruction.Invoke(); }
						catch (Exception ex)
						{
							myLogger.log("Exception while invoking instruction: " + ex, "update()", Logger.severity.ERROR);
							continue;
						}
						switch (CNS.getTypeOfWayDest())
						{
							case NavSettings.TypeOfWayDest.BLOCK:
								log("got a block as a destination: " + CNS.GridDestName, "update()", Logger.severity.INFO);
								return;
							case NavSettings.TypeOfWayDest.OFFSET:
								log("got an offset as a destination: " + CNS.GridDestName + ":" + CNS.BlockDestName + ":" + CNS.destination_offset, "update()", Logger.severity.INFO);
								return;
							case NavSettings.TypeOfWayDest.GRID:
								log("got a grid as a destination: " + CNS.GridDestName, "update()", Logger.severity.INFO);
								return;
							case NavSettings.TypeOfWayDest.COORDINATES:
								log("got a new destination " + CNS.getWayDest(), "update()", Logger.severity.INFO);
								return;
							case NavSettings.TypeOfWayDest.LAND:
								log("got a new landing destination " + CNS.getWayDest(), "update()", Logger.severity.INFO);
								return;
							case NavSettings.TypeOfWayDest.NULL:
								break; // keep searching
							case NavSettings.TypeOfWayDest.WAYPOINT:
								log("got a new waypoint destination (harvesting) " + CNS.getWayDest(), "update()", Logger.severity.INFO);
								return;
							default:
								alwaysLog("got an invalid TypeOfWayDest: " + CNS.getTypeOfWayDest(), "update()", Logger.severity.FATAL);
								return;
						}
						if (CNS.waitUntil.CompareTo(DateTime.UtcNow) > 0)
						{
							myLogger.debugLog("Waiting for " + (CNS.waitUntil - DateTime.UtcNow), "update()", Logger.severity.DEBUG);
							return;
						}
					}
					// at end of allInstructions
					CNS.waitUntilNoCheck = DateTime.UtcNow.AddSeconds(1);
					return;
				}
				else
				{
					// find a remote control with NavSettings allInstructions
					CNS.waitUntilNoCheck = DateTime.UtcNow.AddSeconds(1);
					foreach (Sandbox.ModAPI.IMySlimBlock remoteControlBlock in remoteControlBlocks)
					{
						Sandbox.ModAPI.IMyCubeBlock fatBlock = remoteControlBlock.FatBlock;
						if (remoteControlIsReady(fatBlock))
						{
							if (AIOverride)
							{
								if (currentRCcontrol == null)
								{
									myLogger.debugLog("chose a block for AIOverride", "update()");
									currentRCcontrol = (fatBlock as IMyControllableEntity);
								}
							}
							else
							{
								//	parse display name
								string instructions = fatBlock.getInstructions();
								if (string.IsNullOrWhiteSpace(instructions))
									continue;

								myLogger.debugLog("trying block: " + fatBlock.DisplayNameText, "update()");
								currentRCcontrol = (fatBlock as IMyControllableEntity); // necessary to enqueue actions
								if (myInterpreter == null)
									myInterpreter = new Interpreter(this);
								myInterpreter.enqueueAllActions(fatBlock);
								if (myInterpreter.hasInstructions())
								{
									CNS.startOfCommands();
									log("remote control: " + fatBlock.getNameOnly() + " finished queuing " + myInterpreter.instructionQueue.Count + " instruction", "update()", Logger.severity.TRACE);
									return;
								}
								myLogger.debugLog("failed to enqueue actions from " + fatBlock.getNameOnly(), "update()", Logger.severity.DEBUG);
								currentRCcontrol = null;
								continue;
							}
						}
					}
					// failed to find a ready remote control
				}
			}
		}

		private Interpreter myInterpreter;

		public static bool looseContains(string bigstring, string substring)
		{
			bigstring = bigstring.ToLower().Replace(" ", "");
			substring = substring.ToLower().Replace(" ", "");

			return bigstring.Contains(substring);
		}

		private bool player_controlling = false;

		/// <summary>
		/// checks for is a station, is owned by current session's player, grid exists
		/// </summary>
		/// <returns>true iff it is possible for this grid to navigate</returns>
		public bool gridCanNavigate()
		{
			if (myGrid == null || myGrid.Closed)
			{
				log("grid is gone...", "gridCanNavigate()", Logger.severity.INFO);
				OnClose();
				return false;
			}
			if (myGrid.IsStatic)
				return false;

			if (MyAPIGateway.Players.GetPlayerControllingEntity(myGrid) != null)
			{
				if (!player_controlling)
				{
					IMyPlayer controllingPlayer = MyAPIGateway.Players.GetPlayerControllingEntity(myGrid);
					if (controllingPlayer != null)
					{
						log("player is controlling grid: " + controllingPlayer.DisplayName, "gridCanNavigate()", Logger.severity.TRACE);
						player_controlling = true;
					}
				}
				return false;
			}
			if (player_controlling)
			{
				log("player(s) released controls", "gridCanNavigate()", Logger.severity.TRACE);
				player_controlling = false;
			}

			return true;
		}

		private bool remoteControlIsNotReady = false;

		/// <summary>
		/// checks the working flag, current player owns it, display name has not changed
		/// </summary>
		/// <param name="remoteControl">remote control to check</param>
		/// <returns>true iff the remote control is ready</returns>
		public bool remoteControlIsReady(Sandbox.ModAPI.IMyCubeBlock remoteControl)
		{
			if (remoteControlIsNotReady)
			{
				reset("remote not ready");
				remoteControlIsNotReady = false;
				return false;
			}
			if (remoteControl == null)
			{
				log("no remote control", "remoteControlIsReady()", Logger.severity.TRACE);
				return false;
			}
			if (!remoteControl.IsWorking)
			{
				log("not working", "remoteControlIsReady()", Logger.severity.TRACE);
				return false;
			}
			if (remoteControl.CubeGrid.BigOwners.Count == 0) // no owner
			{
				log("no owner", "remoteControlIsReady()", Logger.severity.TRACE);
				return false;
			}
			if (remoteControl.OwnerId != remoteControl.CubeGrid.BigOwners[0]) // remote control is not owned by grid's owner
			{
				log("remote has different owner", "remoteControlIsReady()", Logger.severity.TRACE);
				return false;
			}
			if (!(remoteControl as Ingame.IMyShipController).ControlThrusters)
			{
				//log("no thruster control", "remoteControlIsReady()", Logger.severity.TRACE);
				return false;
			}

			//myLogger.debugLog("remote is ready: " + remoteControl.DisplayNameText, "remoteControlIsReady()");
			return true;
		}

		public bool remoteControlIsReady(IMyControllableEntity remoteControl)
		{ return remoteControlIsReady(remoteControl as Sandbox.ModAPI.IMyCubeBlock); }

		private void reset(string reason)
		{
			myLogger.debugLog("reset reason = " + reason, "reset()");
			currentRCcontrol = null;
		}

		private DateTime maxRotateTime;
		internal MovementMeasure MM;

		private void navigate()
		{
			myLogger.debugLog("entered navigate()", "navigate()");

			if (currentRCblock == null)
				return;
			if (!remoteControlIsReady(currentRCblock))
			{
				reportState(ReportableState.Off, true);
				reset("remote control is not ready");
				return;
			}

			// before navigate
			MM = new MovementMeasure(this);

			navigateSub();

			// after navigate
			checkStopped();
			myRotator.isRotating();
		}

		private Lander myLand;

		private void navigateSub()
		{
			//log("entered navigate(" + myWaypoint + ")", "navigate()", Logger.severity.TRACE);

			if (CNS.landingState != NavSettings.LANDING.OFF)
			{
				myLand.landGrid(MM); // continue landing
				return;
			}
			if (myLand != null && myLand.targetDirection != null)
			{
				myLand.matchOrientation(); // continue match
				return;
			}
			if (myHarvester.Run())
				return;

			if (!checkAt_wayDest())
				collisionCheckMoveAndRotate();
		}

		internal const int radiusLandWay = 10;

		/// <returns>skip collisionCheckMoveAndRotate()</returns>
		private bool checkAt_wayDest()
		{
			if (CNS.isAMissile)
				return false;
			if (MM.distToWayDest > CNS.destinationRadius)
				return false;
			if (CNS.landLocalBlock != null && MM.distToWayDest > radiusLandWay) // distance to start landing
				return false;

			if (CNS.getTypeOfWayDest() == NavSettings.TypeOfWayDest.WAYPOINT)
			{
				CNS.atWayDest();
				if (CNS.getTypeOfWayDest() == NavSettings.TypeOfWayDest.NULL)
				{
					alwaysLog(Logger.severity.ERROR, "checkAt_wayDest()", "Error no more destinations at Navigator.checkAt_wayDest() // at waypoint");
					fullStop("No more dest");
				}
				else
					log("reached waypoint, next type is " + CNS.getTypeOfWayDest() + ", coords: " + CNS.getWayDest(), "checkAt_wayDest()", Logger.severity.INFO);
				return true;
			}

			if (CNS.match_direction == null && CNS.landLocalBlock == null)
			{
				fullStop("At dest");
				log("reached destination dist = " + MM.distToWayDest + ", proximity = " + CNS.destinationRadius, "checkAt_wayDest()", Logger.severity.INFO);
				CNS.atWayDest();
				return true;
			}
			else
			{
				fullStop("At dest, orient or land");
				if (CNS.landLocalBlock != null)
				{
					log("near dest, start landing. dist=" + MM.distToWayDest + ", radius=" + CNS.destinationRadius, "checkAt_wayDest()", Logger.severity.DEBUG);
					myLand = new Lander(this);
					myLand.landGrid(MM); // start landing
				}
				else // CNS.match_direction != null
				{
					log("near dest, start orient. dist=" + MM.distToWayDest + ", radius=" + CNS.destinationRadius, "checkAt_wayDest()", Logger.severity.DEBUG);
					myLand = new Lander(this);
					myLand.matchOrientation(); // start match
				}
				return true;
			}
		}

		internal void collisionCheckMoveAndRotate()
		{
			if (!CNS.isAMissile)
			{
				myPathfinder_Output = myPathfinder.GetOutput();
				myPathfinder.Run(CNS, getNavigationBlock());
				if (myPathfinder_Output != null)
				{
					//myLogger.debugLog("result: " + myPathfinder_Output.PathfinderResult, "collisionCheckMoveAndRotate()");
					switch (myPathfinder_Output.PathfinderResult)
					{
						case Pathfinder.PathfinderOutput.Result.Incomplete:
							//PathfinderAllowsMovement = true;
							// leave PathfinderAllowsMovement as it was
							break;
						case Pathfinder.PathfinderOutput.Result.Searching_Alt:
							fullStop("searching for a path");
							pathfinderState = ReportableState.Pathfinding;
							PathfinderAllowsMovement = false;
							break;
						case Pathfinder.PathfinderOutput.Result.Alternate_Path:
							myLogger.debugLog("Setting new waypoint: " + myPathfinder_Output.Waypoint, "collisionCheckMoveAndRotate()");
							CNS.setWaypoint(myPathfinder_Output.Waypoint);
							pathfinderState = ReportableState.Path_OK;
							PathfinderAllowsMovement = true;
							break;
						case Pathfinder.PathfinderOutput.Result.Path_Clear:
							//myLogger.debugLog("Path forward is clear", "collisionCheckMoveAndRotate()");
							pathfinderState = ReportableState.Path_OK;
							PathfinderAllowsMovement = true;
							break;
						case Pathfinder.PathfinderOutput.Result.No_Way_Forward:
							fullStop("No Path");
							pathfinderState = ReportableState.No_Path;
							PathfinderAllowsMovement = false;
							return;
						default:
							myLogger.log("Error, invalid case: " + myPathfinder_Output.PathfinderResult, "collisionCheckMoveAndRotate()", Logger.severity.FATAL);
							fullStop("Invalid Pathfinder.PathfinderOutput");
							pathfinderState = ReportableState.No_Path;
							PathfinderAllowsMovement = false;
							return;
					}
				}
			}
			calcMoveAndRotate();
		}

		private bool value_PathfinderAllowsMovement = false;
		/// <summary>Does the pathfinder permit the grid to move?</summary>
		public bool PathfinderAllowsMovement
		{
			get { return CNS.isAMissile || value_PathfinderAllowsMovement; }
			private set { value_PathfinderAllowsMovement = value; }
		}

		private double prevDistToWayDest = float.MaxValue;
		internal bool movingTooSlow = false;

		private void calcMoveAndRotate()
		{
			if (!PathfinderAllowsMovement)
				return;

			SpeedControl.controlSpeed(this);

			switch (CNS.moveState)
			{
				case NavSettings.Moving.MOVING:
					{
						double newDistToWayDest = MM.distToWayDest;
						//myLogger.debugLog("newDistToWayDest = " + newDistToWayDest + ", prevDistToWayDest = " + prevDistToWayDest, "calcMoveAndRotate()");
						if (newDistToWayDest > prevDistToWayDest)
						{
							myLogger.debugLog("Moving away from destination, newDistToWayDest = " + newDistToWayDest + " > prevDistToWayDest = " + prevDistToWayDest, "calcMoveAndRotate()");
							fullStop("moving away from destination");
							return;
						}
						prevDistToWayDest = newDistToWayDest;

						//myLogger.debugLog("movingTooSlow = " + movingTooSlow + ", PathfinderAllowsMovement = " + PathfinderAllowsMovement + ", MM.rotLenSq = " + MM.rotLenSq + ", rotLenSq_startMove = " + rotLenSq_startMove, "calcMoveAndRotate()");
						if (movingTooSlow && PathfinderAllowsMovement //speed up test. missile will never pass this test
							&& MM.rotLenSq < rotLenSq_startMove)
							StartMoveMove();
					}
					break;
				case NavSettings.Moving.STOP_MOVE:
					{
						if (PathfinderAllowsMovement && MM.rotLenSq < myRotator.rotLenSq_stopAndRot && CNS.SpecialFlyingInstructions == NavSettings.SpecialFlying.None)
							StartMoveMove();
					}
					break;
				case NavSettings.Moving.HYBRID:
					{
						//myLogger.debugLog("movingTooSlow = " + movingTooSlow + ", currentMove = " + currentMove, "calcMoveAndRotate()");
						if (movingTooSlow
							|| (currentMove != Vector3.Zero && currentMove != SpeedControl.cruiseForward))
							calcAndMove(true); // continue in current state
						if (MM.rotLenSq < rotLenSq_switchToMove)
						{
							myLogger.debugLog("switching to move", "calcMoveAndRotate()", Logger.severity.DEBUG);
							StartMoveMove();
						}
						break;
					}
				case NavSettings.Moving.SIDELING:
					{
						if (CNS.isAMissile)
						{
							log("missile needs to stop sideling", "calcMoveAndRotate()", Logger.severity.DEBUG);
							fullStop("stop sidel: converted to missile");
							break;
						}
						calcAndMove(true); // continue in current state
						break;
					}
				case NavSettings.Moving.NOT_MOVE:
					{
						if (CNS.rotateState == NavSettings.Rotating.NOT_ROTA)
							MoveIfPossible();
						break;
					}
				default:
					{
						log("Not Yet Implemented, state = " + CNS.moveState, "calcMoveAndRotate()", Logger.severity.ERROR);
						break;
					}
			}

			if (CNS.moveState != NavSettings.Moving.SIDELING)
				calcAndRotate();
		}

		private bool MoveIfPossible()
		{
			if (PathfinderAllowsMovement)
			{
				if (CNS.isAMissile)
				{
					StartMoveHybrid();
					return true;
				}
				if (CNS.SpecialFlyingInstructions == NavSettings.SpecialFlying.Line_SidelForward)// || MM.rotLenSq > rotLenSq_switchToMove)
				{
					StartMoveSidel();
					return true;
				}
				if (CNS.SpecialFlyingInstructions == NavSettings.SpecialFlying.Line_Any)
				{
					StartMoveMove();
					return true;
				}
				if (CNS.landingState == NavSettings.LANDING.OFF && MM.distToWayDest > myGrid.GetLongestDim() + CNS.destinationRadius)
				{
					StartMoveHybrid();
					return true;
				}
				StartMoveSidel();
				return true;
			}
			return false;
		}

		private void StartMoveHybrid()
		{
			calcAndMove(true);
			CNS.moveState = NavSettings.Moving.HYBRID;
		}

		private void StartMoveSidel()
		{
			calcAndMove(true);
			CNS.moveState = NavSettings.Moving.SIDELING;
		}

		private void StartMoveMove()
		{
			calcAndMove();
			CNS.moveState = NavSettings.Moving.MOVING;
		}

		public const float rotLenSq_switchToMove = 0.00762f; // 5°

		/// <summary>
		/// start moving when less than (30°)
		/// </summary>
		public const float rotLenSq_startMove = 0.274f;

		/// <summary>
		/// stop when greater than
		/// </summary>
		private const float onCourse_sidel = 0.1f, onCourse_hybrid = 0.1f;

		private Vector3 moveDirection = Vector3.Zero;

		private void calcAndMove(bool sidel = false)//, bool anyState=false)
		{
			//log("entered calcAndMove("+doSidel+")", "calcAndMove()", Logger.severity.TRACE);
			try
			{
				if (sidel)
				{
					Vector3 worldDisplacement = ((Vector3D)CNS.getWayDest() - (Vector3D)getNavigationBlock().GetPosition());
					RelativeVector3F displacement = RelativeVector3F.createFromWorld(worldDisplacement, myGrid); // Only direction matters, we will normalize later. A multiplier helps prevent precision issues.
					Vector3 course = Vector3.Normalize(displacement.getWorld());
					float offCourse = Vector3.RectangularDistance(course, moveDirection);

					switch (CNS.moveState)
					{
						case NavSettings.Moving.SIDELING:
							{
								if (offCourse < onCourse_sidel)
								{
									if (movingTooSlow)
										goto case NavSettings.Moving.NOT_MOVE;
									return;
								}
								else
								{
									myLogger.debugLog("rectangular distance between " + course + " and " + moveDirection + " is " + offCourse, "calcAndMove()");
									fullStop("change course: sidel");
									return;
								}
							}
						case NavSettings.Moving.HYBRID:
							{
								if (offCourse < onCourse_hybrid)
									goto case NavSettings.Moving.NOT_MOVE;
								else
								{
									myLogger.debugLog("rectangular distance between " + course + " and " + moveDirection + " is " + offCourse, "calcAndMove()");
									CNS.moveState = NavSettings.Moving.MOVING;
									calcAndMove();
									return;
								}
							}
						case NavSettings.Moving.NOT_MOVE:
							{
								RelativeVector3F scaled = currentThrust.scaleByForce(displacement, getNavigationBlock());
								moveOrder(scaled);
								if (CNS.moveState == NavSettings.Moving.NOT_MOVE)
								{
									moveDirection = course;
									log("sideling. wayDest=" + CNS.getWayDest() + ", worldDisplacement=" + worldDisplacement + ", RCdirection=" + course, "calcAndMove()", Logger.severity.DEBUG);
									log("... scaled=" + scaled.getWorld() + ":" + scaled.getLocal() + ":" + scaled.getBlock(getNavigationBlock()), "calcAndMove()", Logger.severity.DEBUG);
								}
								break;
							}
						default:
							{
								alwaysLog("unsuported moveState: " + CNS.moveState, "calcAndMove()", Logger.severity.ERROR);
								return;
							}
					}
				}
				else // not sidel
				{
					Vector3 NavForward = getNavigationBlock().LocalMatrix.Forward;
					Vector3 RemFromNF = Base6Directions.GetVector(currentRCblock.LocalMatrix.GetClosestDirection(ref NavForward));

					moveOrder(RemFromNF); // move forward
					log("forward = " + RemFromNF + ", moving " + MM.distToWayDest + " to " + CNS.getWayDest(), "calcAndMove()", Logger.severity.DEBUG);
				}
			}
			finally
			{
				stoppedMovingAt = DateTime.UtcNow + stoppedAfter;
				movingTooSlow = false;
			}
		}

		internal void calcAndRotate()
		{
			myRotator.calcAndRotate();
			myRotator.calcAndRoll(MM.roll);
		}

		internal void calcAndRoll(float roll)
		{ myRotator.calcAndRoll(roll); }

		private static TimeSpan stoppedAfter = new TimeSpan(0, 0, 0, 1);
		private DateTime stoppedMovingAt;
		private static float stoppedPrecision = 0.2f;

		public bool checkStopped()
		{
			if (CNS.moveState == NavSettings.Moving.NOT_MOVE)
				return true;

			bool isStopped;

			if (MM.movementSpeed == null || MM.movementSpeed > stoppedPrecision)
			{
				stoppedMovingAt = DateTime.UtcNow + stoppedAfter;
				isStopped = false;
			}
			else
			{
				isStopped = DateTime.UtcNow > stoppedMovingAt;
			}

			if (isStopped)
			{
				if (CNS.moveState == NavSettings.Moving.STOP_MOVE)
				{
					CNS.moveState = NavSettings.Moving.NOT_MOVE;
					CNS.clearSpeedInternal();
				}
				else
					fullStop("not moving");
			}

			return isStopped;
		}

		/// <summary>
		/// for other kinds of stop use moveOrder(Vector3.Zero) or similar
		/// </summary>
		internal void fullStop(string reason)
		{
			log("full stop: " + reason, "fullStop()", Logger.severity.INFO);
			currentMove = Vector3.Zero;
			currentRotate = Vector2.Zero;
			currentRoll = 0;
			prevDistToWayDest = float.MaxValue;

			EnableDampeners();
			currentRCcontrol.MoveAndRotateStopped();

			CNS.moveState = NavSettings.Moving.STOP_MOVE;
			CNS.rotateState = NavSettings.Rotating.STOP_ROTA;
		}

		internal Vector3 currentMove = Vector3.One; // for initial fullStop
		internal Vector2 currentRotate = Vector2.Zero;
		internal float currentRoll = 0;

		internal void moveOrder(Vector3 move, bool normalize = true)
		{
			if (normalize)
				move = Vector3.Normalize(move);
			if (!move.IsValid())
				move = Vector3.Zero;
			if (currentMove == move)
				return;
			currentMove = move;
			moveAndRotate();
			if (move != Vector3.Zero)
			{
				myLogger.debugLog("Enabling dampeners", "moveOrder()");
				EnableDampeners();
			}
		}

		internal void moveOrder(RelativeVector3F move, bool normalize = true)
		{
			moveOrder(move.getBlock(currentRCblock), normalize);
		}

		internal void moveAndRotate()
		{
			if (currentMove == Vector3.Zero && currentRotate == Vector2.Zero && currentRoll == 0)
			{
				log("MAR is actually stop", "moveAndRotate()");
				currentRCcontrol.MoveAndRotateStopped();
			}
			else
			{
				if (CNS.moveState != NavSettings.Moving.HYBRID)
					log("doing MAR(" + currentMove + ", " + currentRotate + ", " + currentRoll + ")", "moveAndRotate()");
				currentRCcontrol.MoveAndRotate(currentMove, currentRotate, currentRoll);
			}
		}

		public bool dampenersEnabled()
		{ return ((currentRCcontrol as Ingame.IMyShipController).DampenersOverride) && !currentThrust.disabledThrusters(); }

		internal void DisableReverseThrust()
		{
			switch (CNS.moveState)
			{
				case NavSettings.Moving.HYBRID:
				case NavSettings.Moving.MOVING:
					myLogger.debugLog("disabling reverse thrust", "DisableReverseThrust()");
					EnableDampeners();
					currentThrust.disableThrusters(Base6Directions.GetFlippedDirection(getNavigationBlock().Orientation.Forward));
					break;
				default:
					myLogger.debugLog("disabling dampeners", "DisableReverseThrust()");
					EnableDampeners(false);
					break;
			}
		}

		public void EnableDampeners(bool dampenersOn = true)
		{
			if (dampenersOn)
				currentThrust.enableAllThrusters();

			try
			{
				if ((currentRCcontrol as Ingame.IMyShipController).DampenersOverride != dampenersOn)
				{
					currentRCcontrol.SwitchDamping(); // sometimes SwitchDamping() throws a NullReferenceException while grid is being destroyed
					if (!dampenersOn)
						log("speed control: disabling dampeners. speed=" + MM.movementSpeed + ", cruise=" + CNS.getSpeedCruise() + ", slow=" + CNS.getSpeedSlow(), "setDampeners()", Logger.severity.TRACE);
					else
						log("speed control: enabling dampeners. speed=" + MM.movementSpeed + ", cruise=" + CNS.getSpeedCruise() + ", slow=" + CNS.getSpeedSlow(), "setDampeners()", Logger.severity.TRACE);
				}
			}
			catch (NullReferenceException)
			{ log("setDampeners() threw NullReferenceException", "setDampeners()", Logger.severity.DEBUG); }
		}

		public override string ToString()
		{
			return "Nav:" + myGrid.DisplayName;
		}

		#region Report State

		public enum ReportableState : byte
		{
			None, Off, No_Dest, Waiting,
			Path_OK, Pathfinding, No_Path,
			Rotating, Moving, Hybrid, Sidel, Roll,
			Stop_Move, Stop_Rotate, Stop_Roll,
			H_Ready, Harvest, H_Stuck, H_Back, H_Tunnel,
			Missile, Engaging, Landed, Player, Jump, GET_OUT_OF_SEAT
		};
		/// <summary>The state of the pathinfinder</summary>
		private ReportableState pathfinderState = ReportableState.Path_OK;

		internal bool GET_OUT_OF_SEAT = false;

		internal void reportState(ReportableState newState = ReportableState.Off, bool forced = false)
		{
			if (currentRemoteControl_Value == null)
				return;

			string displayName = (currentRemoteControl_Value as Sandbox.ModAPI.IMyCubeBlock).DisplayNameText;
			if (displayName == null)
			{
				alwaysLog(Logger.severity.WARNING, "reportState()", "cannot report without display name");
				return;
			}

			if (!forced)
				newState = GetState();

			if (newState == ReportableState.None)
				return;

			//// did state actually change?
			//if (newState == currentReportable && newState != ReportableState.Jump && newState != ReportableState.Waiting && newState != ReportableState.Landed) // jump, land, and waiting update times
			//	return;
			//currentReportable = newState;

			// cut old state, if any
			if (displayName[0] == '<')
			{
				int endOfState = displayName.IndexOf('>');
				if (endOfState != -1)
					displayName = displayName.Substring(endOfState + 1);
			}

			// add new state
			StringBuilder newName = new StringBuilder();
			newName.Append('<');
			// error
			if (myInterpreter.instructionErrorIndex != null)
			{
				myLogger.debugLog("adding error, index = " + myInterpreter.instructionErrorIndex, "reportState()");
				newName.Append("ERROR(" + myInterpreter.instructionErrorIndex + ") : ");
			}
			// actual state
			newName.Append(newState);
			// wait time
			if (newState == ReportableState.Waiting || newState == ReportableState.Landed)
			{
				int seconds = (int)Math.Ceiling((CNS.waitUntil - DateTime.UtcNow).TotalSeconds);
				if (seconds >= 0)
				{
					newName.Append(':');
					newName.Append(seconds);
				}
			}

			newName.Append('>');
			newName.Append(displayName);

			(currentRemoteControl_Value as Ingame.IMyTerminalBlock).SetCustomName(newName);
			//log("added ReportableState to RC: " + newName, "reportState()", Logger.severity.TRACE);
		}

		private ReportableState GetState()
		{
			if (CNS.EXIT)
				return ReportableState.Off;
			if (player_controlling)
				return ReportableState.Player;
			if (remoteControlIsNotReady)
				return ReportableState.Off;

			// landing
			if (GET_OUT_OF_SEAT) // must override LANDED
				return ReportableState.GET_OUT_OF_SEAT;
			if (CNS.landingState == NavSettings.LANDING.LOCKED)
				return ReportableState.Landed;

			// pathfinding
			switch (pathfinderState)
			{
				case ReportableState.No_Path:
					return ReportableState.No_Path;
				case ReportableState.Pathfinding:
					return ReportableState.Pathfinding;
			}

			// harvest
			if (myHarvester.IsActive())
				if (myHarvester.HarvestState != ReportableState.H_Ready)
					return myHarvester.HarvestState;

			if (CNS.getWayDest() == null)
				return ReportableState.No_Dest;

			// targeting
			if (CNS.target_locked)
			{
				if (CNS.lockOnTarget == NavSettings.TARGET.ENEMY)
					return ReportableState.Engaging;
				if (CNS.lockOnTarget == NavSettings.TARGET.MISSILE)
					return ReportableState.Missile;
			}

			// moving
			switch (CNS.moveState)
			{
				case NavSettings.Moving.SIDELING:
					return ReportableState.Sidel;
				case NavSettings.Moving.HYBRID:
					return ReportableState.Hybrid;
				case NavSettings.Moving.MOVING:
					return ReportableState.Moving;
			}

			// rotating
			if (CNS.rotateState == NavSettings.Rotating.ROTATING)
				return ReportableState.Rotating;
			if (CNS.rollState == NavSettings.Rolling.ROLLING)
				return ReportableState.Roll;

			// stopping
			if (CNS.moveState == NavSettings.Moving.STOP_MOVE)
				return ReportableState.Stop_Move;
			if (CNS.rotateState == NavSettings.Rotating.STOP_ROTA)
				return ReportableState.Stop_Rotate;
			if (CNS.rollState == NavSettings.Rolling.STOP_ROLL)
				return ReportableState.Stop_Roll;

			return ReportableState.None;
		}

		#endregion
	}
}
