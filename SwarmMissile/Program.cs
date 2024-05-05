using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.GameSystems.Electricity;
using Sandbox.ModAPI.Ingame;
using Sandbox.ModAPI.Interfaces;
using SpaceEngineers.Game.ModAPI.Ingame;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using VRage;
using VRage.Collections;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.GUI.TextPanel;
using VRage.Game.ModAPI.Ingame;
using VRage.Game.ModAPI.Ingame.Utilities;
using VRage.Game.ObjectBuilders.Definitions;
using VRage.Input;
using VRage.Library.Collections;
using VRageMath;
using static IngameScript.Program;

namespace IngameScript
{
    partial class Program : MyGridProgram
    {
        DebugAPI Debug;

        //USER-MANAGED VARS
        string launcherDoorGroupName = "LauncherDoors";
        string launcherWelderGroupName = "LauncherWelders";
        string textPanelName = "LauncherLCD";
        string projectorName = "LauncherProjector";

        int guidanceMode = 0;
        bool overrideGuidanceModeUponLockOn = true;
        int timeForGuidanceActivation = 5; //for how long is the missile to fly straight after launch

        int missileFuelThreshold = 50;

        Vector3D gpsCoordinates = new Vector3D(10146.09, 22318.38, -55107.72);

        //SYSTEM-MANAGED VARS

        public Program()
        {
            Debug = new DebugAPI(this);

            //Obtaining launcher blocks and groups
            textPanel = GridTerminalSystem.GetBlockWithName(textPanelName) as IMyTextPanel;
            hangarProjector = GridTerminalSystem.GetBlockWithName(projectorName) as IMyProjector;
            IMyBlockGroup doors = GridTerminalSystem.GetBlockGroupWithName(launcherDoorGroupName);
            IMyBlockGroup welders = GridTerminalSystem.GetBlockGroupWithName(launcherWelderGroupName);
            if (doors != null)
                doors.GetBlocksOfType(hangarDoors);
            if (welders != null)
                welders.GetBlocksOfType(hangarWelders);

            //Clock used for checking missile build-state and updating display
            Runtime.UpdateFrequency |= UpdateFrequency.Update100;
        }

        public void Save()
        {

        }

        public void Main(string arg, UpdateType updateSource)
        {
            if ((updateSource & UpdateType.Update100) != 0) //Missile is docked in launcher
            {
                if (currentPage == Page.LauncherOptions)
                    UpdateMissileExitClearanceStatus();
                MissileFuelingHandler();
            }
            if((updateSource & UpdateType.Update1) != 0) //Missile has launched
            {
                missile.UpdateShipsInfo();
                missile.mainMissile.UpdateShipsInfo();

                if (missile.currentSplitCandidate > 0)
                {
                    foreach(SubMissile subMissile in missile.subMissiles)
                        subMissile.UpdateShipsInfo();

                    if (!missile.isFullySplit)
                        missile.TrySplitNextSubMissile();

                    missile.UpdateMainMissileFlightPath(gpsCoordinates); 

                    missile.UpdateSplitSubMissileFlightPath(gpsCoordinates);             
                }
                else if (missile.elapsedTime > timeForGuidanceActivation)
                    missile.UpdateEntireMissileFlightPath(gpsCoordinates);
            }

            string textToDisplay = InterfaceHandler(arg);
            textPanel.WriteText(textToDisplay);
        }

        //!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!
        //MISSILE SECTION START
        //!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!

        //********************************************************************************************************************
        //GLOBAL VARS AND STRUCTS DECLARATION START

        EntireMissile missile = new EntireMissile();

        //GLOBAL VARS AND STRUCTS DECLARATION END
        //********************************************************************************************************************

        public class Missile
        {
            public List<IMyShipConnector> connectors = new List<IMyShipConnector>();
            public List<IMyShipMergeBlock> merges = new List<IMyShipMergeBlock>();
            public List<IMyBatteryBlock> batteries = new List<IMyBatteryBlock>();
            public List<IMyGasTank> tanks = new List<IMyGasTank>();
            public List<IMyThrust> thrusters = new List<IMyThrust>();
            public List<IMyGyro> gyros = new List<IMyGyro>();
            public List<IMyCameraBlock> cameras = new List<IMyCameraBlock>();
            public List<IMyWarhead> warheads = new List<IMyWarhead>();

            public IMyCubeGrid CubeGrid;

            public Vector3D lastKnownGravity = new Vector3D(0, 0, 0);

            public MyDetectedEntityInfo detectedEnemy;
            public Vector3D targetOffset = new Vector3D(0, 0, 0);

            public float missileMass, maxThrust;
            public float elapsedTime = 0;

            public float momentumCorrectionStrengh = 1;

            public float evasiveManeuversStrengh = 0;
            public float currentEvasiveManeuversRotation = 0; //this var has to be constantly incremented if evasive man. are in use
            
            public bool IsSteerable()
            {
                float storedPower = 0;
                foreach (IMyBatteryBlock battery in batteries)
                    if (battery != null && battery.IsWorking)
                        storedPower += battery.CurrentStoredPower;

                if (storedPower == 0)
                    return false;

                double storedFuel = 0;
                foreach (IMyGasTank tank in tanks)
                    if (tank != null && tank.IsWorking)
                        storedFuel += tank.FilledRatio;

                if (storedFuel == 0)
                    return false;

                return thrusters.Count(thruster => thruster != null && thruster.IsWorking) > 0 && batteries.Count(battery => battery != null && battery.IsWorking) > 0;
            }

            public int CameraCount()
            {
                return cameras.Count(camera => camera != null && camera.IsWorking);
            }

            public void TryArm()
            {
                foreach (IMyWarhead warhead in warheads)
                    if (warhead != null && warhead.IsWorking)
                        warhead.IsArmed = true;
            }

            //--------------------------------------------------------------------------------------------------------------------
            //GUIDANCE SECTION START

            public void FlyTo(Vector3D enemyPos)
            {
                Vector3D enemyVector = enemyPos - CubeGrid.GetPosition();
                SetGyroscopesToVector(CalculateThrustDirection(enemyVector));
            }

            public Vector3D CalculateThrustDirection(Vector3D desiredDirection)
            {
                float availableThrust = maxThrust;

                Vector3D gravityCorrection = CalculateGravityCorrection(ref availableThrust, desiredDirection);

                Vector3D modifiedDesiredDirection = Vector3D.Normalize(desiredDirection) + CalculateEvasiveManeuvers(desiredDirection);

                Vector3D momentumCorrection = CalculateMomentumCorrection(ref availableThrust, modifiedDesiredDirection, (float)desiredDirection.Length(), CubeGrid.LinearVelocity);

                modifiedDesiredDirection *= availableThrust / missileMass;

                return modifiedDesiredDirection + momentumCorrection + gravityCorrection;
            }

            Vector3D CalculateGravityCorrection(ref float availableThrust, Vector3D desiredDirection)
            {
                //Dont have gravityCorrection interfere with input direction
                Vector3D gravityCorrection = -Vector3D.ProjectOnPlane(ref lastKnownGravity, ref desiredDirection);
                //Subtract force required for gravityCorrection from availableThrust
                availableThrust = Math.Max(0, availableThrust - (float)gravityCorrection.Length() * missileMass);

                return gravityCorrection;
            }

            Vector3D CalculateEvasiveManeuvers(Vector3D desiredDirection)
            {
                Vector3D evasionDirection = Vector3D.CalculatePerpendicularVector(desiredDirection).Rotate(Vector3D.Normalize(desiredDirection), currentEvasiveManeuversRotation);

                ScaleEvasiveManeuvers(ref evasionDirection, desiredDirection);

                return evasionDirection;
            }

            void ScaleEvasiveManeuvers(ref Vector3D evasionDirection, Vector3D desiredDirection)
            {
                int minDistance = 100;
                int maxDistance = 400;

                float clampedDistance = (float)Math.Max(minDistance, Math.Min(maxDistance, (float)desiredDirection.Length()));

                float scalingFactor = Math.Max(0, (maxDistance - clampedDistance) / (maxDistance - minDistance));

                evasionDirection *= (1 - scalingFactor) * evasiveManeuversStrengh;
            }

            Vector3D CalculateMomentumCorrection(ref float availableThrust, Vector3D modifiedDesiredDirection, float distanceToEnemy, Vector3D momentum)
            {
                //Dont have velocityCorrection interfere with desiredDirections
                Vector3D momentumCorrection = -Vector3D.ProjectOnPlane(ref momentum, ref modifiedDesiredDirection);

                //Reduce velocityCorrection strengh to maximum possible in case it's too long
                if ((float)momentumCorrection.Length() * missileMass > availableThrust)
                    momentumCorrection *= availableThrust / ((float)momentumCorrection.Length() * missileMass);

                ScaleMomentumCorrection(ref momentumCorrection, distanceToEnemy);

                //Subtract force required for velocityCorrection from availableThrust
                availableThrust = Math.Max(0, availableThrust - (float)momentumCorrection.Length() * missileMass);

                return momentumCorrection;
            }

            void ScaleMomentumCorrection(ref Vector3D momentumCorrection, float distanceToEnemy)
            {
                int minDistance = 250;
                int maxDistance = 2000;

                float clampedDistance = (float)Math.Max(minDistance, Math.Min(maxDistance, distanceToEnemy));

                float scalingFactor = Math.Max(0, (maxDistance - clampedDistance) / (maxDistance - minDistance));

                momentumCorrection *= scalingFactor * momentumCorrectionStrengh;
            }

            public void SetGyroscopesToVector(Vector3D vector)
            {
                Vector3D targetVector = Vector3D.Normalize(vector);
                foreach (IMyGyro gyro in gyros)
                {
                    gyro.Yaw = 5 * (float)Math.Asin(Vector3D.Dot(targetVector, gyro.WorldMatrix.Right) / targetVector.Length());
                    gyro.Pitch = 5 * (float)Math.Asin(Vector3D.Dot(targetVector, gyro.WorldMatrix.Down) / targetVector.Length());
                }
            }

            //GUIDANCE SECTION END
            //--------------------------------------------------------------------------------------------------------------------
        }

        public class SubMissile : Missile
        {
            public bool isDisconnected = false;
            public bool IsComplete()
            {
                return connectors[0] != null && batteries[0] != null && tanks[0] != null && gyros[0] != null && cameras[0] != null && warheads[0] != null &&
                       connectors[0].IsWorking && batteries[0].IsWorking && tanks[0].IsWorking && gyros[0].IsWorking && cameras[0].IsWorking && warheads[0].IsWorking;
            }

            public void UpdateShipsInfo()
            {
                elapsedTime += 0.0166F;

                missileMass = 0;
                if (IsSteerable())
                {
                    missileMass = 1860.4F;
                    if (connectors[0] == null)
                        missileMass -= 204.8F;
                    if (merges[0] == null)
                        missileMass -= 92.2F;
                    if (warheads[0] == null)
                        missileMass -= 106.2F;
                }

                maxThrust = 0;
                if (thrusters[0] != null && thrusters[0].IsWorking)
                    maxThrust = 98400;
            }

            public void Disconnect()
            {
                isDisconnected = true;
                gyros[0].Roll = 0;
                merges[0].Enabled = false;
                connectors[0].Disconnect();
            }
        }

        public class MainMissile : Missile
        {
            public List<IMyRemoteControl> remotes = new List<IMyRemoteControl>();
            public List<IMyTurretControlBlock> controllers = new List<IMyTurretControlBlock>();
            public List<IMyShipConnector> dockingConnectors = new List<IMyShipConnector>();
            public List<IMyShipMergeBlock> dockingMerges = new List<IMyShipMergeBlock>();
            public List<IMyShipMergeBlock> clusterMerges = new List<IMyShipMergeBlock>();

            public IMyRemoteControl TryGetWorkingRemote()
            {
                return remotes.FirstOrDefault(remote => remote != null && remote.IsWorking);
            }

            public IMyTurretControlBlock TryGetWorkingController()
            {
                return controllers.FirstOrDefault(controller => controller != null && controller.IsWorking);
            }

            public MatrixD TryGetWorldMatrix()
            {
                IMyRemoteControl remote = TryGetWorkingRemote();
                if (remote != null)
                    return remote.WorldMatrix;

                IMyGyro gyro = gyros.FirstOrDefault(block => block != null && block.IsWorking);
                return gyro.WorldMatrix;
            }

            public void TryUpdateGravity()
            {
                IMyRemoteControl remote = TryGetWorkingRemote();
                if (remote == null)
                    return;

                lastKnownGravity = remote.GetNaturalGravity();
            }

            public void UpdateShipsInfo()
            {
                elapsedTime += 0.0166F;

                IMyRemoteControl remote = TryGetWorkingRemote();
                if (remote != null)
                    missileMass = remote.CalculateShipMass().BaseMass;

                maxThrust = 0;
                foreach (IMyThrust thruster in thrusters)
                    if (thruster != null && thruster.IsWorking)
                        maxThrust += 98400;
            }
        }

        public class EntireMissile : MainMissile
        {
            public MainMissile mainMissile;
            public List<SubMissile> subMissiles = new List<SubMissile>();

            public float lastSplitTime;
            public int currentSplitCandidate = 0;
            float timeToStartSplitting = 1.5F; //Start splitting after 1.5 seconds of rotation start
            float splitInterval = 0.03F; //Split every 2 Update1

            public bool isFullySplit = false;

            public int swarmSplitDistance = 4000; //Distance from target to initiate split procedure

            public void UpdateEntireMissileFlightPath(Vector3D enemyPos)
            {
                currentEvasiveManeuversRotation += 0.05F;

                TryUpdateGravity();
                FlyTo(enemyPos);

                if ((enemyPos - CubeGrid.GetPosition()).Length() < swarmSplitDistance)
                    PrepareSwarmSplitProcedure();
            }

            public void UpdateMainMissileFlightPath(Vector3D enemyPos)
            {
                if (!mainMissile.IsSteerable())
                    return;

                mainMissile.evasiveManeuversStrengh = 0F;
                mainMissile.momentumCorrectionStrengh = 1;

                mainMissile.TryUpdateGravity();
                mainMissile.FlyTo(enemyPos);

                if ((enemyPos - mainMissile.thrusters[0].CubeGrid.GetPosition()).Length() < 200)
                    mainMissile.TryArm();
            }

            public void UpdateSplitSubMissileFlightPath(Vector3D enemyPos) //This function needs a LOT of breaking down
            {
                bool isMissileStuck = false; //is submissile stuck after demerging
                int rot = 0; //var used for generating points on the ring for the submissiles
                foreach (SubMissile subMissile in subMissiles)
                {
                    if (subMissile.CubeGrid == null) //cubegrid isnt updated instantly after merge.Enabled = false, therefore the program checks whether it's finally different from mainMissile
                    {
                        if (subMissile.merges[0].CubeGrid != CubeGrid)
                            subMissile.CubeGrid = subMissile.merges[0].CubeGrid;
                    }
                    else if (subMissile.isDisconnected && subMissile.IsSteerable()) //If the cubegrid has updated and the sub missile is good to go
                    {
                        subMissile.lastKnownGravity = mainMissile.lastKnownGravity;

                        Vector3D mainMissileForwardVector = TryGetWorldMatrix().Forward;
                        if (mainMissile.IsSteerable() && mainMissile.TryGetWorkingRemote() != null && (enemyPos - mainMissile.CubeGrid.GetPosition()).Length() > 2000) //If the main missile is further than 2km from target
                        {
                            Vector3D missileToMissileVector = mainMissile.CubeGrid.GetPosition() - subMissile.CubeGrid.GetPosition();
                            Vector3D missileToMissileSideOnly = Vector3D.ProjectOnPlane(ref missileToMissileVector, ref mainMissileForwardVector);

                            if (missileToMissileVector.Length() < 10) //the sub missile is stuck, have it try and face away from the main missile (they dont do it very well, friction is too big) 
                            {
                                subMissile.SetGyroscopesToVector(-missileToMissileSideOnly);
                                isMissileStuck = true;
                            }
                            else //Try and line up in ring formation (note - momentum correction strengh and distance of the ring points from main missile plays a big part in how chaotic sub missile movements are)
                            {
                                rot += 360 / subMissiles.Count();
                                Vector3D circlePoint = Vector3D.CalculatePerpendicularVector(mainMissileForwardVector).Rotate(Vector3D.Normalize(mainMissileForwardVector), rot * Math.PI / 180 + elapsedTime / 2) * 10;
                                Vector3D missileToPointVector = mainMissile.CubeGrid.GetPosition() + circlePoint - subMissile.CubeGrid.GetPosition();
                                Vector3D missileToPointSideOnly = Vector3D.ProjectOnPlane(ref missileToPointVector, ref mainMissileForwardVector);
                                Vector3D destination = CubeGrid.GetPosition() + Vector3D.Normalize(mainMissileForwardVector) * 1000 + circlePoint; // * 1000 is to generate distance of the ring from main missile, so the submissile dont go completely sideways, only a little bit
                                subMissile.FlyTo(destination);
                                if ((enemyPos - subMissile.CubeGrid.GetPosition()).Length() < 200)
                                    subMissile.TryArm();
                            }
                        }
                        else //The main missile is close to target, have the submissile ignore the formation and instead attack the target (ideally this line will be removed, and sub missiles will stick in formation until impact)
                            subMissile.FlyTo(enemyPos + subMissile.targetOffset);  
                    }
                }
                if (!isMissileStuck && currentSplitCandidate > subMissiles.Count) //No sub missile is stuck and the split procedure has ended
                    EndSwarmSplitProcedure();
                else if (currentSplitCandidate > subMissiles.Count) //The split procedure has ended, but some mf got stuck, wiggle him off (This also needs improvement, perhaps try to incorporate linear velocity instead of just relying on angular vel.)
                    Wiggle();
            }

            public void Disconnect()
            {
                foreach (IMyShipConnector connector in dockingConnectors)
                    connector.Disconnect();

                foreach (IMyShipMergeBlock merge in dockingMerges)
                    merge.Enabled = false;
            }

            public void PrepareSwarmSplitProcedure()
            {
                foreach (IMyGyro gyro in gyros)
                    gyro.Roll = 60;

                lastSplitTime = elapsedTime;
                currentSplitCandidate++;
            }

            public void TrySplitNextSubMissile()
            {
                if (currentSplitCandidate == 1 && elapsedTime - lastSplitTime < timeToStartSplitting)
                    return;

                if (elapsedTime - lastSplitTime < splitInterval)
                    return;

                if (currentSplitCandidate <= subMissiles.Count)
                {
                    subMissiles[currentSplitCandidate - 1].Disconnect();
                    lastSplitTime = elapsedTime;
                    currentSplitCandidate++;
                }
            }

            public void Wiggle()
            {
                if(elapsedTime - lastSplitTime > 3F)
                {
                    foreach (IMyGyro gyro in mainMissile.gyros)
                        gyro.Roll = -gyro.Roll;
                    lastSplitTime = elapsedTime;
                }  
            }

            public void EndSwarmSplitProcedure()
            {
                isFullySplit = true;
                foreach (IMyGyro gyro in mainMissile.gyros)
                    gyro.Roll = 0;
            }
        }

        //********************************************************************************************************************
        //LAUNCH PROCEDURE START

        void Launch()
        {
            List<SubMissile> subMissiles = InitializeSubMissilesObjectList();
            MainMissile mainMissile = InitializeMainMissileObject();
            InitializeEntireMissileObject(mainMissile, subMissiles);

            StartMissile();
            missile.Disconnect();

            Runtime.UpdateFrequency &= ~UpdateFrequency.Update100;
            Runtime.UpdateFrequency |= UpdateFrequency.Update1;
        }

        MainMissile InitializeMainMissileObject()
        {
            MainMissile mainMissile = new MainMissile();

            List<IMyCameraBlock> cameras = new List<IMyCameraBlock>();
            List<IMyWarhead> warheads = new List<IMyWarhead>();
            List<IMyThrust> thrusters = new List<IMyThrust>();
            List<IMyGyro> gyros = new List<IMyGyro>();
            List<IMyRemoteControl> remotes = new List<IMyRemoteControl>();
            List<IMyTurretControlBlock> controllers = new List<IMyTurretControlBlock>();
            List<IMyGasTank> tanks = new List<IMyGasTank>();
            List<IMyBatteryBlock> batteries = new List<IMyBatteryBlock>();
            List<IMyShipConnector> connectors = new List<IMyShipConnector>();
            List<IMyShipConnector> dockingConnectors = new List<IMyShipConnector>();
            List<IMyShipMergeBlock> merges = new List<IMyShipMergeBlock>();
            List<IMyShipMergeBlock> dockingMerges = new List<IMyShipMergeBlock>();
            List<IMyShipMergeBlock> clusterMerges = new List<IMyShipMergeBlock>();

            IMyBlockGroup group;
            group = GridTerminalSystem.GetBlockGroupWithName("MainMissileBatteries");
            group.GetBlocksOfType(batteries);
            group = GridTerminalSystem.GetBlockGroupWithName("MainMissileClusterMerges");
            group.GetBlocksOfType(clusterMerges);
            group = GridTerminalSystem.GetBlockGroupWithName("MainMissileConnectors");
            group.GetBlocksOfType(connectors);
            group = GridTerminalSystem.GetBlockGroupWithName("MainMissileDockingConnectors");
            group.GetBlocksOfType(dockingConnectors);
            group = GridTerminalSystem.GetBlockGroupWithName("MainMissileDockingMerges");
            group.GetBlocksOfType(dockingMerges);
            group = GridTerminalSystem.GetBlockGroupWithName("MainMissileGyros");
            group.GetBlocksOfType(gyros);
            group = GridTerminalSystem.GetBlockGroupWithName("MainMissileHydrogenTanks");
            group.GetBlocksOfType(tanks);
            group = GridTerminalSystem.GetBlockGroupWithName("MainMissileMerges");
            group.GetBlocksOfType(merges);
            group = GridTerminalSystem.GetBlockGroupWithName("MainMissileRemoteControls");
            group.GetBlocksOfType(remotes);
            group = GridTerminalSystem.GetBlockGroupWithName("MainMissileThrusters");
            group.GetBlocksOfType(thrusters);
            group = GridTerminalSystem.GetBlockGroupWithName("MainMissileTurretControllers");
            group.GetBlocksOfType(controllers);
            group = GridTerminalSystem.GetBlockGroupWithName("MainMissileWarheads");
            group.GetBlocksOfType(warheads);
            group = GridTerminalSystem.GetBlockGroupWithName("MainMissileCameras");
            group.GetBlocksOfType(cameras);

            mainMissile.batteries = batteries;
            mainMissile.clusterMerges = clusterMerges;
            mainMissile.connectors = connectors;
            mainMissile.dockingConnectors = dockingConnectors;
            mainMissile.dockingMerges = dockingMerges;
            mainMissile.gyros = gyros;
            mainMissile.tanks = tanks;
            mainMissile.merges = merges;
            mainMissile.remotes = remotes;
            mainMissile.thrusters = thrusters;
            mainMissile.controllers = controllers;
            mainMissile.warheads = warheads;
            mainMissile.cameras = cameras;

            mainMissile.CubeGrid = mainMissile.dockingMerges[0].CubeGrid;

            return mainMissile;
        }

        List<SubMissile> InitializeSubMissilesObjectList()
        {
            List<SubMissile> subMissiles = new List<SubMissile>();

            List<IMyThrust> subThrusters = new List<IMyThrust>();
            List<IMyShipConnector> subConnectors = new List<IMyShipConnector>();
            List<IMyShipMergeBlock> subMerges = new List<IMyShipMergeBlock>();
            List<IMyGyro> subGyros = new List<IMyGyro>();
            List<IMyGasTank> subTanks = new List<IMyGasTank>();
            List<IMyBatteryBlock> subBatteries = new List<IMyBatteryBlock>();
            List<IMyCameraBlock> subCameras = new List<IMyCameraBlock>();
            List<IMyWarhead> subWarheads = new List<IMyWarhead>();

            IMyBlockGroup group;
            group = GridTerminalSystem.GetBlockGroupWithName("SubMissileBatteries");
            group.GetBlocksOfType(subBatteries);
            group = GridTerminalSystem.GetBlockGroupWithName("SubMissileCameras");
            group.GetBlocksOfType(subCameras);
            group = GridTerminalSystem.GetBlockGroupWithName("SubMissileConnectors");
            group.GetBlocksOfType(subConnectors);
            group = GridTerminalSystem.GetBlockGroupWithName("SubMissileGyros");
            group.GetBlocksOfType(subGyros);
            group = GridTerminalSystem.GetBlockGroupWithName("SubMissileHydrogenTanks");
            group.GetBlocksOfType(subTanks);
            group = GridTerminalSystem.GetBlockGroupWithName("SubMissileMerges");
            group.GetBlocksOfType(subMerges);
            group = GridTerminalSystem.GetBlockGroupWithName("SubMissileThrusters");
            group.GetBlocksOfType(subThrusters);
            group = GridTerminalSystem.GetBlockGroupWithName("SubMissileWarheads");
            group.GetBlocksOfType(subWarheads);

            SubMissile[] tempArray = new SubMissile[48];

            Random rnd = new Random();
            for (int i = 0; i < 48; i++)
            {
                tempArray[i] = new SubMissile();

                tempArray[i].targetOffset.X = rnd.Next(-10, 11); //Change these values to tighten/spread the attack
                tempArray[i].targetOffset.Y = rnd.Next(-10, 11);
                tempArray[i].targetOffset.Z = rnd.Next(-10, 11);
            }
                
            //Below for each subcomponent we check the number at the end of its name and initialize it in a proper tempArray index, this probably can be improved
            foreach (IMyThrust thruster in subThrusters)
            {
                int firstDigitIndex = thruster.CustomName.IndexOfAny("0123456789".ToCharArray());
                int partNumber = int.Parse(thruster.CustomName.Substring(firstDigitIndex));
                tempArray[partNumber - 1].thrusters.Add(thruster);
            }
            foreach (IMyShipConnector connector in subConnectors)
            {
                int firstDigitIndex = connector.CustomName.IndexOfAny("0123456789".ToCharArray());
                int partNumber = int.Parse(connector.CustomName.Substring(firstDigitIndex));
                tempArray[partNumber - 1].connectors.Add(connector);
            }
            foreach (IMyShipMergeBlock merge in subMerges)
            {
                int firstDigitIndex = merge.CustomName.IndexOfAny("0123456789".ToCharArray());
                int partNumber = int.Parse(merge.CustomName.Substring(firstDigitIndex));
                tempArray[partNumber - 1].merges.Add(merge);
            }
            foreach (IMyGyro gyro in subGyros)
            {
                int firstDigitIndex = gyro.CustomName.IndexOfAny("0123456789".ToCharArray());
                int partNumber = int.Parse(gyro.CustomName.Substring(firstDigitIndex));
                tempArray[partNumber - 1].gyros.Add(gyro);
            }
            foreach (IMyGasTank tank in subTanks)
            {
                int firstDigitIndex = tank.CustomName.IndexOfAny("0123456789".ToCharArray());
                int partNumber = int.Parse(tank.CustomName.Substring(firstDigitIndex));
                tempArray[partNumber - 1].tanks.Add(tank);
            }
            foreach (IMyBatteryBlock battery in subBatteries)
            {
                int firstDigitIndex = battery.CustomName.IndexOfAny("0123456789".ToCharArray());
                int partNumber = int.Parse(battery.CustomName.Substring(firstDigitIndex));
                tempArray[partNumber - 1].batteries.Add(battery);
            } 
            foreach (IMyCameraBlock camera in subCameras)
            {
                int firstDigitIndex = camera.CustomName.IndexOfAny("0123456789".ToCharArray());
                int partNumber = int.Parse(camera.CustomName.Substring(firstDigitIndex));
                tempArray[partNumber - 1].cameras.Add(camera);
            }     
            foreach (IMyWarhead warhead in subWarheads)
            {
                int firstDigitIndex = warhead.CustomName.IndexOfAny("0123456789".ToCharArray());
                int partNumber = int.Parse(warhead.CustomName.Substring(firstDigitIndex));
                tempArray[partNumber - 1].warheads.Add(warhead);
            }
                
            for (int i = 0; i < 48; i++)
                if (tempArray[i].IsComplete())
                    subMissiles.Add(tempArray[i]);

            return subMissiles.OrderBy(obj => obj.merges[0].CustomName).ToList();
        }

        void InitializeEntireMissileObject(MainMissile mainMissile, List<SubMissile> subMissiles)
        {
            List<IMyCameraBlock> cameras = new List<IMyCameraBlock>();

            IMyBlockGroup group;
            group = GridTerminalSystem.GetBlockGroupWithName("MissileFrontCameras");
            group.GetBlocksOfType(cameras);

            missile.batteries = mainMissile.batteries.ToList();
            missile.clusterMerges = mainMissile.clusterMerges;
            missile.connectors = mainMissile.connectors.ToList();
            missile.dockingConnectors = mainMissile.dockingConnectors;
            missile.dockingMerges = mainMissile.dockingMerges;
            missile.gyros = mainMissile.gyros.ToList();
            missile.tanks = mainMissile.tanks.ToList();
            missile.merges = mainMissile.merges.ToList();
            missile.remotes = mainMissile.remotes;
            missile.thrusters = mainMissile.thrusters.ToList();
            missile.controllers = mainMissile.controllers;
            missile.warheads = mainMissile.warheads.ToList();
            missile.cameras = cameras;

            foreach (SubMissile subMissile in subMissiles)
            {
                missile.batteries.AddRange(subMissile.batteries);
                missile.connectors.AddRange(subMissile.connectors);
                missile.gyros.AddRange(subMissile.gyros);
                missile.tanks.AddRange(subMissile.tanks);
                missile.merges.AddRange(subMissile.merges);
                missile.thrusters.AddRange(subMissile.thrusters);
                missile.warheads.AddRange(subMissile.warheads);
            }

            missile.mainMissile = mainMissile;
            missile.subMissiles = subMissiles.ToList();

            missile.CubeGrid = mainMissile.CubeGrid;
        }

        void StartMissile()
        {
            foreach (IMyThrust thruster in missile.thrusters)
            {
                thruster.ThrustOverride = thruster.MaxThrust;
                thruster.Enabled = true;
            }
            foreach (IMyShipConnector connector in missile.connectors)
                connector.Connect();
            foreach (IMyShipMergeBlock merge in missile.merges)
                merge.Enabled = true;
            foreach (IMyGyro gyro in missile.gyros)
                gyro.GyroOverride = true;
            foreach (IMyGasTank tank in missile.tanks)
                tank.Stockpile = false;
        }

        //LAUNCH PROCEDURE END
        //********************************************************************************************************************

        //!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!
        //MISSILE SECTION END
        //!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!

        //!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!
        //MISSILE LAUNCHER SECTION START
        //!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!

        //********************************************************************************************************************
        //GLOBAL VARS AND STRUCTS DECLARATION START

        int currentChoice = 1;
        enum Page
        {
            LauncherOptions,
            MissileOptions,
            GuidanceOptions,
            ConfirmationPanel
        }
        Page currentPage = Page.LauncherOptions;

        IMyTextPanel textPanel;
        List<IMyAirtightHangarDoor> hangarDoors = new List<IMyAirtightHangarDoor>();
        List<IMyShipWelder> hangarWelders = new List<IMyShipWelder>();
        IMyProjector hangarProjector;

        int modifiedCoordinate = 0;
        bool isMissileFueling = true;
        MyDetectedEntityInfo missileExitClearance;
        string[,] turretAndCameraNames = new string[0, 0];
        int selectedTurretOrCameraIndex;
        bool Update1UpdatedOnce = false;
        bool isLaunched = false;

        //GLOBAL VARS AND STRUCTS DECLARATION END
        //********************************************************************************************************************

        //********************************************************************************************************************
        //PAGE SELECTOR START

        string InterfaceHandler(string arg)
        {
            string textToDisplay;
            switch (currentPage)
            {
                case Page.LauncherOptions:
                    textToDisplay = LauncherOptionsPageHandler(arg);
                    break;
                case Page.MissileOptions:
                    textToDisplay = MissileOptionsPageHandler(arg);
                    break;
                case Page.GuidanceOptions:
                    textToDisplay = GuidanceOptionsPageHandler(arg);
                    break;
                case Page.ConfirmationPanel:
                    textToDisplay = ConfirmationPanelPageHandler(arg);
                    break;
                default:
                    textToDisplay = "Error: Non-existent page selected";
                    break;
            }
            return textToDisplay;
        }

        //PAGE SELECTOR END
        //********************************************************************************************************************

        //********************************************************************************************************************
        //LAUNCHER OPTIONS PAGE (BACKEND + FRONTEND) START

        string LauncherOptionsPageHandler(string arg)
        {
            string textToDisplay = "";
            switch (arg)
            {
                case "nextRecursive":
                    if (currentChoice > 6)
                        currentChoice = 0;
                    if (currentChoice - 4 == (int)currentPage)
                    {
                        currentChoice++;
                        textToDisplay = LauncherOptionsPageHandler("nextRecursive");
                    }
                    else
                        textToDisplay = LauncherOptionsPageHandler("RecursionEnd");
                    break;
                case "prevRecursive":
                    if (currentChoice < 0)
                        currentChoice = 6;
                    if (currentChoice - 4 == (int)currentPage)
                    {
                        currentChoice--;
                        textToDisplay = LauncherOptionsPageHandler("prevRecursive");
                    }
                    else
                        textToDisplay = LauncherOptionsPageHandler("RecursionEnd");
                    break;
                case "next":
                    currentChoice++;
                    textToDisplay = LauncherOptionsPageHandler("nextRecursive");
                    break;
                case "prev":
                    currentChoice--;
                    textToDisplay = LauncherOptionsPageHandler("prevRecursive");
                    break;
                case "enter":
                    if (currentChoice == 0)
                        isMissileFueling = !isMissileFueling;
                    else if (currentChoice == 1)
                    {
                        if (hangarDoors.Count > 0)
                            if (hangarDoors[0].Status == DoorStatus.Open || hangarDoors[0].Status == DoorStatus.Opening)
                                foreach (IMyAirtightHangarDoor door in hangarDoors)
                                    door.CloseDoor();
                            else
                                foreach (IMyAirtightHangarDoor door in hangarDoors)
                                    door.OpenDoor();
                    }
                    else if (currentChoice == 2)
                        foreach (IMyShipWelder welder in hangarWelders)
                            welder.Enabled = !welder.Enabled;
                    else if (currentChoice == 3)
                        currentPage = Page.ConfirmationPanel;
                    else
                    {
                        currentPage = (Page)(currentChoice - 4);
                        currentChoice = 0;
                    }
                    textToDisplay = InterfaceHandler("");
                    break;
                default:
                    textToDisplay = DrawLauncherOptions();
                    break;
            }
            return textToDisplay;
        }

        string DrawLauncherOptions()
        {
            string textToDisplay = "-------------------<Missile Info>-------------------\n";
            textToDisplay += "Missile state: Built in " + ((hangarProjector.TotalBlocks - hangarProjector.RemainingBlocks) * 100 / hangarProjector.TotalBlocks) + "%\n";
            textToDisplay += $"{(currentChoice == 0 ? "-> " : "")}Missile fueled in: {GetMinimalGasTankFuelCount()}% ({(GetMinimalGasTankFuelCount() >= missileFuelThreshold ? ">= Threshold!" : (isMissileFueling ? "In progress..." : "Stopped"))})\n"; //Add option to control fueling when below 100%
            textToDisplay += "Clearance: " + (missileExitClearance.IsEmpty() ? "Clear!" : "Obstructed(" + missileExitClearance.Type + " - " + Vector3D.Distance(textPanel.GetPosition(), missileExitClearance.HitPosition.Value).ToString("0") + "m)") + "\n";
            textToDisplay += "------------------<Launcher Info>------------------\n";
            if (hangarDoors.Count > 0)
                textToDisplay += $"{(currentChoice == 1 ? "-> " : "")}Launcher doors: {(hangarDoors.Count > 0 ? hangarDoors[0].Status.ToString() : "Not detected")}\n";
            textToDisplay += $"{(currentChoice == 2 ? "-> " : "")}Launcher welders: {(hangarWelders.Count > 0 ? (hangarWelders[0].IsActivated ? "Enabled" : "Disabled") : "Not detected")}\n";
            textToDisplay += "------------------<Launch Panel>------------------\n";
            textToDisplay += "\n" + (currentChoice == 3 ? "-> " : "") + "LAUNCH" + (currentChoice == 3 ? " <-" : "") + "\n\n";
            textToDisplay += "-----------------<Page Selection>-----------------\n";
            Array pages = Enum.GetValues(typeof(Page));
            foreach (Page page in pages)
                if (page != Page.ConfirmationPanel)
                    textToDisplay += $"{((int)page == (int)currentPage ? "# " : (int)page == currentChoice - 4 ? "-> " : "")}{page}{((int)page == (int)currentPage ? " #" : "")}\n";
            return textToDisplay;
        }

        //LAUNCHER OPTIONS PAGE (BACKEND + FRONTEND) END
        //********************************************************************************************************************

        //********************************************************************************************************************
        //MISSILE OPTIONS PAGE (BACKEND + FRONTEND) START

        string MissileOptionsPageHandler(string arg)
        {
            string textToDisplay = "";
            switch (arg)
            {
                case "nextRecursive":
                    if (currentChoice > 5)
                        currentChoice = 0;
                    if (currentChoice - 3 == (int)currentPage)
                    {
                        currentChoice++;
                        textToDisplay = MissileOptionsPageHandler("nextRecursive");
                    }
                    else
                        textToDisplay = MissileOptionsPageHandler("RecursionEnd");
                    break;
                case "prevRecursive":
                    if (currentChoice < 0)
                        currentChoice = 5;
                    if (currentChoice - 3 == (int)currentPage)
                    {
                        currentChoice--;
                        textToDisplay = MissileOptionsPageHandler("prevRecursive");
                    }
                    else
                        textToDisplay = MissileOptionsPageHandler("RecursionEnd");
                    break;
                case "next":
                    currentChoice++;
                    textToDisplay = MissileOptionsPageHandler("nextRecursive");
                    break;
                case "prev":
                    currentChoice--;
                    textToDisplay = MissileOptionsPageHandler("prevRecursive");
                    break;
                case "enter":
                    if (currentChoice == 0)
                        overrideGuidanceModeUponLockOn = !overrideGuidanceModeUponLockOn;
                    else if (currentChoice == 1)
                        timeForGuidanceActivation = Math.Max(1, (timeForGuidanceActivation + 1) % 11);
                    else if (currentChoice == 2)
                        missileFuelThreshold = Math.Max(5, (missileFuelThreshold + 5) % 105);
                    else
                    {
                        currentPage = (Page)(currentChoice - 3);
                        currentChoice = 0;
                    }
                    textToDisplay = InterfaceHandler("");
                    break;
                default:
                    textToDisplay = DrawMissileOptions();
                    break;
            }
            return textToDisplay;
        }

        string DrawMissileOptions()
        {
            string textToDisplay = "----------------<Missile Options>----------------\n";
            textToDisplay += (currentChoice == 0 ? "-> " : "") + "Override guidance upon lock: " + overrideGuidanceModeUponLockOn.ToString() + "\n";
            textToDisplay += (currentChoice == 1 ? "-> " : "") + "Time for guidance activation: " + timeForGuidanceActivation.ToString() + "s\n";
            textToDisplay += (currentChoice == 2 ? "-> " : "") + "Missile fuel threshold: " + missileFuelThreshold.ToString() + "%\n";
            textToDisplay += "-----------------<Page Selection>-----------------\n";
            Array pages = Enum.GetValues(typeof(Page));
            foreach (Page page in pages)
                if (page != Page.ConfirmationPanel)
                    textToDisplay += $"{((int)page == (int)currentPage ? "# " : (int)page == currentChoice - 3 ? "-> " : "")}{page}{((int)page == (int)currentPage ? " #" : "")}\n";
            return textToDisplay;
        }

        //MISSILE OPTIONS PAGE (BACKEND + FRONTEND) END
        //********************************************************************************************************************

        //********************************************************************************************************************
        //GUIDANCE OPTIONS PAGE (BACKEND + FRONTEND) START

        string GuidanceOptionsPageHandler(string arg)
        {
            string textToDisplay = "";
            switch (arg)
            {
                case "nextRecursive":
                    if (currentChoice > 6 + (guidanceMode == 0 ? 0 : (guidanceMode == 1 ? 10 : (turretAndCameraNames.Length / 2))))
                        currentChoice = 0;
                    if (currentChoice - 4 - (guidanceMode == 0 ? 0 : (guidanceMode == 1 ? 10 : (turretAndCameraNames.Length / 2))) == (int)currentPage || currentChoice == guidanceMode || (guidanceMode == 1 && currentChoice - 4 == modifiedCoordinate) || ((guidanceMode == 2 || guidanceMode == 3) && currentChoice - 4 == selectedTurretOrCameraIndex))
                    {
                        currentChoice++;
                        textToDisplay = GuidanceOptionsPageHandler("nextRecursive");
                    }
                    else
                        textToDisplay = GuidanceOptionsPageHandler("RecursionEnd");
                    break;
                case "prevRecursive":
                    if (currentChoice < 0)
                        currentChoice = 6 + (guidanceMode == 0 ? 0 : (guidanceMode == 1 ? 10 : (turretAndCameraNames.Length / 2)));
                    if (currentChoice - 4 - (guidanceMode == 0 ? 0 : (guidanceMode == 1 ? 10 : (turretAndCameraNames.Length / 2))) == (int)currentPage || currentChoice == guidanceMode || (guidanceMode == 1 && currentChoice - 4 == modifiedCoordinate) || ((guidanceMode == 2 || guidanceMode == 3) && currentChoice - 4 == selectedTurretOrCameraIndex))
                    {
                        currentChoice--;
                        textToDisplay = GuidanceOptionsPageHandler("prevRecursive");
                    }
                    else
                        textToDisplay = GuidanceOptionsPageHandler("RecursionEnd");
                    break;
                case "next":
                    currentChoice++;
                    textToDisplay = GuidanceOptionsPageHandler("nextRecursive");
                    break;
                case "prev":
                    currentChoice--;
                    textToDisplay = GuidanceOptionsPageHandler("prevRecursive");
                    break;
                case "enter":
                    if (currentChoice < 4)
                    {
                        guidanceMode = currentChoice;
                        if (guidanceMode == 2)
                            GetLauncherTurretNames();
                        else if (guidanceMode == 3)
                            GetLauncherCameraNamesStart();
                    }
                    else if (guidanceMode == 1)
                    {
                        if (currentChoice < 7)
                            modifiedCoordinate = currentChoice - 4;
                        else if (currentChoice < 14)
                        {
                            if (modifiedCoordinate == 0)
                                gpsCoordinates.X += (gpsCoordinates.X / (int)Math.Pow(10, 13 - currentChoice)) % 10 == 9 ? -9 * (int)Math.Pow(10, 13 - currentChoice) : (int)Math.Pow(10, 13 - currentChoice);
                            else if (modifiedCoordinate == 1)
                                gpsCoordinates.Y += (gpsCoordinates.Y / (int)Math.Pow(10, 13 - currentChoice)) % 10 == 9 ? -9 * (int)Math.Pow(10, 13 - currentChoice) : (int)Math.Pow(10, 13 - currentChoice);
                            else
                                gpsCoordinates.Z += (gpsCoordinates.Z / (int)Math.Pow(10, 13 - currentChoice)) % 10 == 9 ? -9 * (int)Math.Pow(10, 13 - currentChoice) : (int)Math.Pow(10, 13 - currentChoice);
                        }
                        else
                        {
                            currentPage = (Page)(currentChoice - 14);
                            currentChoice = 0;
                        }
                    }
                    else if (guidanceMode == 2 || guidanceMode == 3)
                    {
                        if (currentChoice < 4 + (turretAndCameraNames.Length / 2))
                        {
                            selectedTurretOrCameraIndex = currentChoice - 4;
                        }
                        else
                        {
                            currentPage = (Page)(currentChoice - 4 - (turretAndCameraNames.Length / 2));
                            currentChoice = 0;
                        }
                    }
                    else
                    {
                        currentPage = (Page)(currentChoice - 4);
                        currentChoice = 0;
                    }
                    textToDisplay = InterfaceHandler("");
                    break;
                default:
                    textToDisplay = DrawGuidanceOptions();
                    break;
            }
            return textToDisplay;
        }

        string DrawGuidanceOptions()
        {
            string textToDisplay = "-----------------<Guidance Mode>-----------------\n";
            textToDisplay += $"{(guidanceMode == 0 ? "# " : currentChoice == 0 ? "-> " : "")}Unguided{(guidanceMode == 0 ? " #" : "")}\n";
            textToDisplay += $"{(guidanceMode == 1 ? "# " : currentChoice == 1 ? "-> " : "")}GPS coordinates guided{(guidanceMode == 1 ? " #" : "")}\n";
            textToDisplay += $"{(guidanceMode == 2 ? "# " : currentChoice == 2 ? "-> " : "")}Launching platform Turret AI (800m){(guidanceMode == 2 ? " #" : "")}\n";
            textToDisplay += $"{(guidanceMode == 3 ? "# " : currentChoice == 3 ? "-> " : "")}Camera/Turret guided{(guidanceMode == 3 ? " #" : "")}\n";
            if (guidanceMode == 1)
            {
                textToDisplay += "------------<Coordinate Configuration>------------\n";
                textToDisplay += $"{(modifiedCoordinate == 0 ? "# " : currentChoice == 4 ? "-> " : "")}X: {(gpsCoordinates.X.ToString() + (modifiedCoordinate == 0 ? " #" : ""))}\n";
                textToDisplay += $"{(modifiedCoordinate == 1 ? "# " : currentChoice == 5 ? "-> " : "")}Y: {(gpsCoordinates.Y.ToString() + (modifiedCoordinate == 1 ? " #" : ""))}\n";
                textToDisplay += $"{(modifiedCoordinate == 2 ? "# " : currentChoice == 6 ? "-> " : "")}Z: {(gpsCoordinates.Z.ToString() + (modifiedCoordinate == 2 ? " #" : ""))}\n";
                textToDisplay += "~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~\n";
                textToDisplay += (currentChoice == 7 ? "-> " : "") + "Scroll 1000000\n";
                textToDisplay += (currentChoice == 8 ? "-> " : "") + "Scroll 100000\n";
                textToDisplay += (currentChoice == 9 ? "-> " : "") + "Scroll 10000\n";
                textToDisplay += (currentChoice == 10 ? "-> " : "") + "Scroll 1000\n";
                textToDisplay += (currentChoice == 11 ? "-> " : "") + "Scroll 100\n";
                textToDisplay += (currentChoice == 12 ? "-> " : "") + "Scroll 10\n";
                textToDisplay += (currentChoice == 13 ? "-> " : "") + "Scroll 1\n";
            }
            else if (guidanceMode == 2 || guidanceMode == 3)
            {
                textToDisplay += guidanceMode == 2 ? "---------------<Turret Selection>---------------\n" : "---------------<Camera Selection>---------------\n";
                for (int i = 0; i < turretAndCameraNames.Length / 2; i++)
                    textToDisplay += (selectedTurretOrCameraIndex == i ? "# " : (currentChoice == 4 + i) ? "-> " : "") + turretAndCameraNames[i, 0] + (selectedTurretOrCameraIndex == i ? " #" : "") + "\n";
            }
            textToDisplay += "-----------------<Page Selection>-----------------\n";
            Array pages = Enum.GetValues(typeof(Page));
            foreach (Page page in pages)
                if (page != Page.ConfirmationPanel)
                    textToDisplay += $"{((int)page == (int)currentPage ? "# " : ((int)page == currentChoice - 4 - (guidanceMode == 0 ? 0 : (guidanceMode == 1 ? 10 : (turretAndCameraNames.Length / 2)))) ? "-> " : "")}{page}{((int)page == (int)currentPage ? " #" : "")}\n";
            string[] linesOfText = textToDisplay.Split('\n');
            textToDisplay = string.Join("\n", linesOfText.Skip(Math.Max(0, linesOfText.Length - 30 + currentChoice)));
            return textToDisplay;
        }

        //GUIDANCE OPTIONS PAGE (BACKEND + FRONTEND) END
        //********************************************************************************************************************

        //********************************************************************************************************************
        //CONFIRMATION PANEL PAGE (BACKEND + FRONTEND) START

        string ConfirmationPanelPageHandler(string arg)
        {
            string textToDisplay = "";
            switch (arg)
            {
                case "next":
                case "prev":
                    currentChoice = (currentChoice + 1) % 2;
                    textToDisplay = ConfirmationPanelPageHandler("");
                    break;
                case "enter":
                    if (currentChoice == 0)
                        Launch();
                    currentPage = Page.LauncherOptions;
                    textToDisplay = InterfaceHandler("");
                    break;
                default:
                    textToDisplay = DrawConfirmationPanel();
                    break;
            }
            return textToDisplay;
        }

        string DrawConfirmationPanel()
        {
            List<string> criticalAlerts = new List<string>();
            if (hangarProjector.RemainingBlocks > 0)
                criticalAlerts.Add("Missile not fully built! (" + ((hangarProjector.TotalBlocks - hangarProjector.RemainingBlocks) * 100 / hangarProjector.TotalBlocks).ToString() + "%)\n");
            if (!missileExitClearance.IsEmpty())
                criticalAlerts.Add($"Missile exit not clear! ({missileExitClearance.Type} - {Vector3D.Distance(hangarProjector.GetPosition(), missileExitClearance.HitPosition.Value).ToString("0")}m)\n");
            if (GetMinimalGasTankFuelCount() < 25)
                criticalAlerts.Add($"Missile fuel low! (" + (GetMinimalGasTankFuelCount() * 100 / 48).ToString() + "%)\n");

            List<string> warnings = new List<string>();
            if (guidanceMode == 1 && gpsCoordinates.Length() == 0)
                warnings.Add("No GPS cords selected (GPS = 0,0,0)\n");
            else if (guidanceMode == 2 || guidanceMode == 3)
            {
                IMyTerminalBlock textPanel = GridTerminalSystem.GetBlockWithName(textPanelName) as IMyTerminalBlock;
                if (textPanel == null || selectedTurretOrCameraIndex == -1)
                    warnings.Add("Leading turret/camera not set or null\n");
            }

            if (warnings.Count + criticalAlerts.Count == 0 && currentChoice > 1)
                currentChoice = 0;
            else if (currentChoice > 1)
                currentChoice = 1;

            string textToDisplay = "";
            if (criticalAlerts.Count > 0)
            {
                textToDisplay += "------------------<Critical Alerts>-------------------\n";
                for (int i = 0; i < criticalAlerts.Count; i++)
                    textToDisplay += (i + 1).ToString() + ". " + criticalAlerts[i];
            }
            if (warnings.Count > 0)
            {
                textToDisplay += "---------------------<Warnings>---------------------\n";
                for (int i = 0; i < warnings.Count; i++)
                    textToDisplay += (criticalAlerts.Count + i + 1).ToString() + ". " + warnings[i];
            }
            textToDisplay += "--------------<Launch Confirmation>--------------\n";
            textToDisplay += "                  Proceed with launch?\n";
            textToDisplay += $"{(currentChoice == 0 ? "-> " : "    ")}Confirm{(currentChoice == 0 ? " <-" : "    ")}                             " +
                             $"{(currentChoice == 1 ? "-> " : "    ")}Cancel{(currentChoice == 1 ? " <-" : "   ")}";

            return textToDisplay;
        }

        //CONFIRMATION PANEL PAGE (BACKEND + FRONTEND) END
        //********************************************************************************************************************

        void UpdateMissileExitClearanceStatus()
        {
            List<IMyCameraBlock> cameras = new List<IMyCameraBlock>();
            IMyBlockGroup group = GridTerminalSystem.GetBlockGroupWithName("SwarmFrontCameras");
            if (group == null)
                return;
            group.GetBlocksOfType(cameras);

            MyDetectedEntityInfo enemy;
            foreach (IMyCameraBlock camera in cameras)
            {
                camera.EnableRaycast = true;
                enemy = camera.Raycast(100 * timeForGuidanceActivation + 100, 0, 0);
                if (!enemy.IsEmpty())
                {
                    missileExitClearance = enemy;
                    return;
                }
            }

            enemy = cameras[0].Raycast(0, 0, 0);
            missileExitClearance = enemy;
        }

        void MissileFuelingHandler()
        {
            List<IMyShipConnector> connectors = new List<IMyShipConnector>();
            IMyBlockGroup group = GridTerminalSystem.GetBlockGroupWithName("MainMissileDockingConnectors");
            if (group == null)
                return;
            group.GetBlocksOfType(connectors);  

            if (GetMinimalGasTankFuelCount() >= missileFuelThreshold)
            {
                foreach (IMyShipConnector connector in connectors)
                    connector.Disconnect();
                return;
            }

            foreach (IMyShipConnector connector in connectors)
                if (isMissileFueling)
                    connector.Connect();
                else
                    connector.Disconnect();
        }

        int GetMinimalGasTankFuelCount()
        {
            List<IMyGasTank> subTanks = new List<IMyGasTank>();
            IMyBlockGroup group = GridTerminalSystem.GetBlockGroupWithName("SubMissileHydrogenTanks");
            if (group == null)
                return 0;
            group.GetBlocksOfType(subTanks);

            List<IMyGasTank> mainTanks = new List<IMyGasTank>();
            group = GridTerminalSystem.GetBlockGroupWithName("MainMissileHydrogenTanks");
            group.GetBlocksOfType(mainTanks);

            List<IMyGasTank> allTanks = subTanks.Concat(mainTanks).ToList();

            int minimumFuelCount = 100;
            foreach (IMyGasTank tank in allTanks)
                minimumFuelCount = Math.Min((int)(tank.FilledRatio * 100), minimumFuelCount);

            return minimumFuelCount;
        }

        //*************************************************************************************************************************** FUCKED UP SECTION. BURN IT BEFORE IT LAYS EGGS!
        //Its used for extracting and displaying cameras/turrets on the screen, but only those which are not part of the missile
        //Ive used IsSameConstructAs to perform this, but in order for this approach to work, the missile has to be connected by connectors only
        //So what I do (and its cursed) is I demerge the missile, quickly connect it via connectors (connector arent always connected, due to missileFuelingHandler turning them on and off when needed) and then extract the data
        //This is clangy and laggy and surely theres a way to perform this stuff without touching merges/connectors
        //One way could be to simply Union lists and remove common elements

        void GetLauncherTurretNames()
        {
            var blocks = new List<IMyTerminalBlock>();
            GridTerminalSystem.GetBlocksOfType<IMyLargeTurretBase>(blocks);
            List<IMyLargeTurretBase> turrets = blocks.ConvertAll(x => (IMyLargeTurretBase)x).Where(turret => turret.IsSameConstructAs(textPanel)).ToList();
            blocks = new List<IMyTerminalBlock>();
            GridTerminalSystem.GetBlocksOfType<IMyTurretControlBlock>(blocks);
            List<IMyTurretControlBlock> controllers = blocks.ConvertAll(x => (IMyTurretControlBlock)x).Where(controller => controller.IsSameConstructAs(textPanel)).ToList();

            turretAndCameraNames = new string[turrets.Count + controllers.Count, 2];
            for (int i = 0; i < turrets.Count; i++)
            {
                turretAndCameraNames[i, 0] = turrets[i].CustomName;
                turretAndCameraNames[i, 1] = turrets[i].BlockDefinition.ToString();
            }
            for (int i = 0; i < controllers.Count; i++)
            {
                turretAndCameraNames[i, 0] = controllers[i].CustomName;
                turretAndCameraNames[i, 1] = controllers[i].BlockDefinition.ToString();
            }

            selectedTurretOrCameraIndex = -1;
            if (turretAndCameraNames.Length > 0)
                selectedTurretOrCameraIndex = 0;
        }

        void GetLauncherCameraNamesStart()
        {
            IMyShipConnector launcherConnector1 = GridTerminalSystem.GetBlockWithName("MainLauncherConnector1") as IMyShipConnector;
            IMyShipConnector launcherConnector2 = GridTerminalSystem.GetBlockWithName("MainLauncherConnector2") as IMyShipConnector;
            IMyShipMergeBlock launcherMerge1 = GridTerminalSystem.GetBlockWithName("MainLauncherMerge1") as IMyShipMergeBlock;
            IMyShipMergeBlock launcherMerge2 = GridTerminalSystem.GetBlockWithName("MainLauncherMerge2") as IMyShipMergeBlock;
            launcherConnector1.Connect();
            launcherConnector2.Connect();
            launcherMerge1.Enabled = launcherMerge2.Enabled = false;

            Runtime.UpdateFrequency |= UpdateFrequency.Update1;
        }

        void GetLauncherCameraNamesEnd()
        {
            var blocks = new List<IMyTerminalBlock>();
            GridTerminalSystem.GetBlocksOfType<IMyLargeTurretBase>(blocks);
            List<IMyLargeTurretBase> turrets = blocks.ConvertAll(x => (IMyLargeTurretBase)x).Where(turret => turret.IsSameConstructAs(textPanel)).ToList();
            blocks = new List<IMyTerminalBlock>();
            GridTerminalSystem.GetBlocksOfType<IMyCameraBlock>(blocks);
            List<IMyCameraBlock> cameras = blocks.ConvertAll(x => (IMyCameraBlock)x).Where(camera => camera.IsSameConstructAs(textPanel)).ToList();

            turretAndCameraNames = new string[turrets.Count + cameras.Count, 2];
            for (int i = 0; i < turrets.Count; i++)
            {
                turretAndCameraNames[i, 0] = turrets[i].CustomName;
                turretAndCameraNames[i, 1] = turrets[i].BlockDefinition.ToString();
            }
            for (int i = 0; i < cameras.Count; i++)
            {
                turretAndCameraNames[i, 0] = cameras[i].CustomName;
                turretAndCameraNames[i, 1] = cameras[i].BlockDefinition.ToString();
            }

            selectedTurretOrCameraIndex = -1;
            if (turretAndCameraNames.Length > 0)
                selectedTurretOrCameraIndex = 0;

            IMyShipMergeBlock launcherMerge1 = GridTerminalSystem.GetBlockWithName("MainLauncherMerge1") as IMyShipMergeBlock;
            IMyShipMergeBlock launcherMerge2 = GridTerminalSystem.GetBlockWithName("MainLauncherMerge2") as IMyShipMergeBlock;
            launcherMerge1.Enabled = launcherMerge2.Enabled = true;

            Runtime.UpdateFrequency &= ~UpdateFrequency.Update1;
            Update1UpdatedOnce = false;
        }

        //***************************************************************************************************************************

        Vector3I TryParseGps(string arg)
        {
            string[] splitString = arg.Split(':');

            double x, y, z;
            if (double.TryParse(splitString[2], out x) && double.TryParse(splitString[3], out y) && double.TryParse(splitString[4], out z))
                return new Vector3I((int)x, (int)y, (int)z);

            return new Vector3I(0, 0, 0);
        }

        //!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!
        //MISSILE LAUNCHER SECTION END
        //!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!

        //!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!
        //DEBUG API SECTION START (required SE debug api mod)
        //!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!

        public class DebugAPI
        {
            public readonly bool ModDetected;

            /// <summary>
            /// Changing this will affect OnTop draw for all future draws that don't have it specified.
            /// </summary>
            public bool DefaultOnTop;

            /// <summary>
            /// Recommended to be used at start of Main(), unless you wish to draw things persistently and remove them manually.
            /// <para>Removes everything except AdjustNumber and chat messages.</para>
            /// </summary>
            public void RemoveDraw() => _removeDraw?.Invoke(_pb);
            Action<IMyProgrammableBlock> _removeDraw;

            /// <summary>
            /// Removes everything that was added by this API (except chat messages), including DeclareAdjustNumber()!
            /// <para>For calling in Main() you should use <see cref="RemoveDraw"/> instead.</para>
            /// </summary>
            public void RemoveAll() => _removeAll?.Invoke(_pb);
            Action<IMyProgrammableBlock> _removeAll;

            /// <summary>
            /// You can store the integer returned by other methods then remove it with this when you wish.
            /// <para>Or you can not use this at all and call <see cref="RemoveDraw"/> on every Main() so that your drawn things live a single PB run.</para>
            /// </summary>
            public void Remove(int id) => _remove?.Invoke(_pb, id);
            Action<IMyProgrammableBlock, int> _remove;

            public int DrawPoint(Vector3D origin, Color color, float radius = 0.2f, float seconds = DefaultSeconds, bool? onTop = null) => _point?.Invoke(_pb, origin, color, radius, seconds, onTop ?? DefaultOnTop) ?? -1;
            Func<IMyProgrammableBlock, Vector3D, Color, float, float, bool, int> _point;

            public int DrawLine(Vector3D start, Vector3D end, Color color, float thickness = DefaultThickness, float seconds = DefaultSeconds, bool? onTop = null) => _line?.Invoke(_pb, start, end, color, thickness, seconds, onTop ?? DefaultOnTop) ?? -1;
            Func<IMyProgrammableBlock, Vector3D, Vector3D, Color, float, float, bool, int> _line;

            public int DrawAABB(BoundingBoxD bb, Color color, Style style = Style.Wireframe, float thickness = DefaultThickness, float seconds = DefaultSeconds, bool? onTop = null) => _aabb?.Invoke(_pb, bb, color, (int)style, thickness, seconds, onTop ?? DefaultOnTop) ?? -1;
            Func<IMyProgrammableBlock, BoundingBoxD, Color, int, float, float, bool, int> _aabb;

            public int DrawOBB(MyOrientedBoundingBoxD obb, Color color, Style style = Style.Wireframe, float thickness = DefaultThickness, float seconds = DefaultSeconds, bool? onTop = null) => _obb?.Invoke(_pb, obb, color, (int)style, thickness, seconds, onTop ?? DefaultOnTop) ?? -1;
            Func<IMyProgrammableBlock, MyOrientedBoundingBoxD, Color, int, float, float, bool, int> _obb;

            public int DrawSphere(BoundingSphereD sphere, Color color, Style style = Style.Wireframe, float thickness = DefaultThickness, int lineEveryDegrees = 15, float seconds = DefaultSeconds, bool? onTop = null) => _sphere?.Invoke(_pb, sphere, color, (int)style, thickness, lineEveryDegrees, seconds, onTop ?? DefaultOnTop) ?? -1;
            Func<IMyProgrammableBlock, BoundingSphereD, Color, int, float, int, float, bool, int> _sphere;

            public int DrawMatrix(MatrixD matrix, float length = 1f, float thickness = DefaultThickness, float seconds = DefaultSeconds, bool? onTop = null) => _matrix?.Invoke(_pb, matrix, length, thickness, seconds, onTop ?? DefaultOnTop) ?? -1;
            Func<IMyProgrammableBlock, MatrixD, float, float, float, bool, int> _matrix;

            /// <summary>
            /// Adds a HUD marker for a world position.
            /// <para>White is used if <paramref name="color"/> is null.</para>
            /// </summary>
            public int DrawGPS(string name, Vector3D origin, Color? color = null, float seconds = DefaultSeconds) => _gps?.Invoke(_pb, name, origin, color, seconds) ?? -1;
            Func<IMyProgrammableBlock, string, Vector3D, Color?, float, int> _gps;

            /// <summary>
            /// Adds a notification center on screen. Do not give 0 or lower <paramref name="seconds"/>.
            /// </summary>
            public int PrintHUD(string message, Font font = Font.Debug, float seconds = 2) => _printHUD?.Invoke(_pb, message, font.ToString(), seconds) ?? -1;
            Func<IMyProgrammableBlock, string, string, float, int> _printHUD;

            /// <summary>
            /// Shows a message in chat as if sent by the PB (or whoever you want the sender to be)
            /// <para>If <paramref name="sender"/> is null, the PB's CustomName is used.</para>
            /// <para>The <paramref name="font"/> affects the fontface and color of the entire message, while <paramref name="senderColor"/> only affects the sender name's color.</para>
            /// </summary>
            public void PrintChat(string message, string sender = null, Color? senderColor = null, Font font = Font.Debug) => _chat?.Invoke(_pb, message, sender, senderColor, font.ToString());
            Action<IMyProgrammableBlock, string, string, Color?, string> _chat;

            /// <summary>
            /// Used for realtime adjustments, allows you to hold the specified key/button with mouse scroll in order to adjust the <paramref name="initial"/> number by <paramref name="step"/> amount.
            /// <para>Add this once at start then store the returned id, then use that id with <see cref="GetAdjustNumber(int)"/>.</para>
            /// </summary>
            public void DeclareAdjustNumber(out int id, double initial, double step = 0.05, Input modifier = Input.Control, string label = null) => id = _adjustNumber?.Invoke(_pb, initial, step, modifier.ToString(), label) ?? -1;
            Func<IMyProgrammableBlock, double, double, string, string, int> _adjustNumber;

            /// <summary>
            /// See description for: <see cref="DeclareAdjustNumber(double, double, Input, string)"/>.
            /// <para>The <paramref name="noModDefault"/> is returned when the mod is not present.</para>
            /// </summary>
            public double GetAdjustNumber(int id, double noModDefault = 1) => _getAdjustNumber?.Invoke(_pb, id) ?? noModDefault;
            Func<IMyProgrammableBlock, int, double> _getAdjustNumber;

            /// <summary>
            /// Gets simulation tick since this session started. Returns -1 if mod is not present.
            /// </summary>
            public int GetTick() => _tick?.Invoke() ?? -1;
            Func<int> _tick;

            /// <summary>
            /// Gets time from Stopwatch which is accurate to nanoseconds, can be used to measure code execution time.
            /// Returns TimeSpan.Zero if mod is not present.
            /// </summary>
            public TimeSpan GetTimestamp() => _timestamp?.Invoke() ?? TimeSpan.Zero;
            Func<TimeSpan> _timestamp;

            /// <summary>
            /// Use with a using() statement to measure a chunk of code and get the time difference in a callback.
            /// <code>
            /// using(Debug.Measure((t) => Echo($"diff={t}")))
            /// {
            ///    // code to measure
            /// }
            /// </code>
            /// This simply calls <see cref="GetTimestamp"/> before and after the inside code.
            /// </summary>
            public MeasureToken Measure(Action<TimeSpan> call) => new MeasureToken(this, call);

            /// <summary>
            /// <see cref="Measure(Action{TimeSpan})"/>
            /// </summary>
            public MeasureToken Measure(string prefix) => new MeasureToken(this, (t) => PrintHUD($"{prefix} {t.TotalMilliseconds} ms"));

            public struct MeasureToken : IDisposable
            {
                DebugAPI API;
                TimeSpan Start;
                Action<TimeSpan> Callback;

                public MeasureToken(DebugAPI api, Action<TimeSpan> call)
                {
                    API = api;
                    Callback = call;
                    Start = API.GetTimestamp();
                }

                public void Dispose()
                {
                    Callback?.Invoke(API.GetTimestamp() - Start);
                }
            }

            public enum Style { Solid, Wireframe, SolidAndWireframe }
            public enum Input { MouseLeftButton, MouseRightButton, MouseMiddleButton, MouseExtraButton1, MouseExtraButton2, LeftShift, RightShift, LeftControl, RightControl, LeftAlt, RightAlt, Tab, Shift, Control, Alt, Space, PageUp, PageDown, End, Home, Insert, Delete, Left, Up, Right, Down, D0, D1, D2, D3, D4, D5, D6, D7, D8, D9, A, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P, Q, R, S, T, U, V, W, X, Y, Z, NumPad0, NumPad1, NumPad2, NumPad3, NumPad4, NumPad5, NumPad6, NumPad7, NumPad8, NumPad9, Multiply, Add, Separator, Subtract, Decimal, Divide, F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12 }
            public enum Font { Debug, White, Red, Green, Blue, DarkBlue }

            const float DefaultThickness = 0.02f;
            const float DefaultSeconds = -1;

            IMyProgrammableBlock _pb;

            /// <summary>
            /// NOTE: if mod is not present then methods will simply not do anything, therefore you can leave the methods in your released code.
            /// </summary>
            /// <param name="program">pass `this`.</param>
            /// <param name="drawOnTopDefault">set the default for onTop on all objects that have such an option.</param>
            public DebugAPI(MyGridProgram program, bool drawOnTopDefault = false)
            {
                if (program == null) throw new Exception("Pass `this` into the API, not null.");

                DefaultOnTop = drawOnTopDefault;
                _pb = program.Me;

                var methods = _pb.GetProperty("DebugAPI")?.As<IReadOnlyDictionary<string, Delegate>>()?.GetValue(_pb);
                if (methods != null)
                {
                    Assign(out _removeAll, methods["RemoveAll"]);
                    Assign(out _removeDraw, methods["RemoveDraw"]);
                    Assign(out _remove, methods["Remove"]);
                    Assign(out _point, methods["Point"]);
                    Assign(out _line, methods["Line"]);
                    Assign(out _aabb, methods["AABB"]);
                    Assign(out _obb, methods["OBB"]);
                    Assign(out _sphere, methods["Sphere"]);
                    Assign(out _matrix, methods["Matrix"]);
                    Assign(out _gps, methods["GPS"]);
                    Assign(out _printHUD, methods["HUDNotification"]);
                    Assign(out _chat, methods["Chat"]);
                    Assign(out _adjustNumber, methods["DeclareAdjustNumber"]);
                    Assign(out _getAdjustNumber, methods["GetAdjustNumber"]);
                    Assign(out _tick, methods["Tick"]);
                    Assign(out _timestamp, methods["Timestamp"]);

                    RemoveAll(); // cleanup from past compilations on this same PB

                    ModDetected = true;
                }
            }

            void Assign<T>(out T field, object method) => field = (T)method;
        }

        //!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!
        //DEBUG API SECTION END
        //!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!_!
    }
}
