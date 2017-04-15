﻿using AR.Drone.Avionics;
using AR.Drone.Avionics.Objectives;
using AR.Drone.Client;
using AR.Drone.Client.Command;
using AR.Drone.Data.Navigation;
using AutoPilotApp.Common;
using AutoPilotApp.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AutoPilotApp.Pilot
{
    public class Controller : ObservableObject
    {
        DroneClient droneClient;
        private Missions currentMission;

        public Missions CurrentMission
        {
            get { return currentMission; }
        }

        Autopilot autoPilot;
        AnalyzerOuput analyzer;
        Config config;
        ControllerCalculations calculator;
        public Controller(DroneClient client, AnalyzerOuput output, Config configuration)
        {
            analyzer = output;
            droneClient = client;
            autoPilot = new Autopilot(client);
            config = configuration;
            calculator = new ControllerCalculations(output.FovSize);
        }

        public void EmergencyStop()
        {
            stopAutopilot();

            droneClient.Emergency();
        }

        public bool Stop()
        {
            try
            {
                stopAutopilot();
                if (loop != null)
                {
                    loop.Wait(1000);
                    loop = null;
                }
            }
            catch (Exception ex)
            {
                Logger.LogException(ex);
                return false;
            }
            return true;
        }

        void stopAutopilot()
        {
            if (cancellationTokenSource != null)
            {
                lock (this)
                {
                    if (cancellationTokenSource != null)
                    {
                        cancellationTokenSource.Cancel();
                        cancellationTokenSource = null;
                    }
                }
            }
        }

        public void Start(Missions mission)
        {
            stopAutopilot();

            currentMission = mission;

            RaisePropertyChanged(nameof(CurrentMission));

            startAutopilot();

        }

        CancellationTokenSource cancellationTokenSource;
        Task loop;
        void startAutopilot()
        {
            if (cancellationTokenSource != null)
                return;
            lock (this)
            {
                if (cancellationTokenSource != null)
                    return;

                cancellationTokenSource = new CancellationTokenSource();
                loop = Task.Run(() => Loop(cancellationTokenSource.Token), cancellationTokenSource.Token);
            }
        }

        void Loop(CancellationToken token)
        {
            step = 0;
            Stopwatch sw1 = null;
            bool pic = false;

            while (!token.IsCancellationRequested)
            {
                switch (currentMission)
                {
                    case Missions.Objective:
                    case Missions.Home:
                        switch (step)
                        {
                            case 0:
                                flyToObjective();
                                break;
                            case 1:
                                if (sw1 == null)
                                {
                                    sw1 = Stopwatch.StartNew();
                                    Logger.Log("Objective Reached, Taking picture", LogLevel.Event);
                                }
                                if (sw1.ElapsedMilliseconds < 4000)
                                {
                                    if(sw1.ElapsedMilliseconds>2000 && !pic)
                                    {
                                        pic = true;
                                        sendPicture();
                                    }
                                    hover();
                                }
                                else
                                {
                                    Logger.LogInfo("Step 2");

                                    step = 2;
                                    droneClient.Land();
                                    Stop();
                                }
                                break;
                            default:
                                hover();
                                break;
                        }
                        break;
                    case Missions.AttendeesPicture:
                    default:
                        {
                            CurrentCommand = "Hover";
                            hover();
                            break;
                        }
                }
                Task.Delay(10).Wait();
            }
        }

        private void sendPicture()
        {

        }

        private void hover()
        {
            droneClient.Hover();
        }

        private string currentCommand;

        public string CurrentCommand
        {
            get { return currentCommand; }
            set { Set(ref currentCommand, value); }
        }

        int step;

        void flyToObjective()
        {
            bool flight = true;
            var state = droneClient.NavigationData.State;
            if (state.HasFlag(NavigationState.Emergency))
                return;

            if (flight && state.HasFlag(NavigationState.Landed))
            {
                analyzer.ResultingCommand = "takeof";
                analyzer.Navigation.SetMovement(Movements.TakeOff);
                droneClient.Takeoff();
                return;
            }
            if (analyzer.Detected)
            {
                if (droneClient.NavigationData.Altitude > 0.5 || !flight)
                {
                    var width = analyzer.FovSize.Width / 2f;
                    var change = width - analyzer.Center.X;

                    var distance = calculator.GetDistance(new System.Drawing.Size(analyzer.Width, analyzer.Height));
                    var diff = calculator.GetDiff(distance);// (analyzer.Distance / analyzer.FovSize.Width) * 7.5f;

                    var roll = config.SpaceConfig.TurnSpeed * 2f / 3f;
                    var yaw = config.SpaceConfig.TurnSpeed / 3f;

                    if (change > diff)
                    {
                        if (flight)
                            droneClient.Progress(FlightMode.Progressive, roll: 0-roll, yaw: 0-yaw);
                        analyzer.ResultingCommand = $"right {change} {diff}";
                        analyzer.Navigation.SetMovement(Movements.Right);

                    }
                    else if  (change < (0-diff))
                    {
                        if (flight)
                            droneClient.Progress(FlightMode.Progressive, roll: roll, yaw: yaw);
                        analyzer.ResultingCommand = $"left {change} {diff}";
                        analyzer.Navigation.SetMovement(Movements.Left);

                    }
                    else
                    {
                        if (distance < config.SpaceConfig.MaxDistance)
                        {
                            analyzer.ResultingCommand = $"pitch {change}";
                            analyzer.Navigation.SetMovement(Movements.Ahead);

                            if (flight)
                                droneClient.Progress(FlightMode.Progressive, pitch: -0.05f);
                        }
                        else
                        {
                            analyzer.ResultingCommand = "hover";
                            analyzer.Navigation.SetMovement(Movements.Hover);

                            step = 1;
                            Logger.LogInfo("Step 1");
                        }
                    }
                }
                else
                {
                    analyzer.Navigation.SetMovement(Movements.Up);

                    droneClient.Progress(FlightMode.Progressive, gaz: 0.25f);
                    analyzer.ResultingCommand = $"altitude {droneClient.NavigationData.Altitude}";
                }
            }
            else
            {
                analyzer.ResultingCommand = "seek";

                if (flight)
                    droneClient.Progress(FlightMode.Progressive, yaw: 0.10f);
            }

        }
    }
}
