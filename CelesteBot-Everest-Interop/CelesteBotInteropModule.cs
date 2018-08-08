﻿using Celeste.Mod;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Monocle;
using MonoMod;
using MonoMod.RuntimeDetour;
using MonoMod.Utils;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Logger = Celeste.Mod.Logger;

namespace CelesteBot_Everest_Interop
{
    public class CelesteBotInteropModule : EverestModule
    {
        public static CelesteBotInteropModule Instance;

        public override Type SettingsType => typeof(CelesteBotModuleSettings);
        public static CelesteBotModuleSettings Settings => (CelesteBotModuleSettings)Instance._Settings;

        public static string ModLogKey = "celeste-bot";

        public static Population population;
        public static CelestePlayer CurrentPlayer;

        private static int buffer = 0; // The number of frames to wait when setting a new current player

        public static ArrayList innovationHistory = new ArrayList();

        public static bool DrawPlayer { get { return !ShowNothing && Settings.ShowPlayerBrain; } set { } }
        public static bool DrawFitness { get { return !ShowNothing && Settings.ShowPlayerFitness; } set { } }
        public static bool DrawDetails { get { return !ShowNothing && Settings.ShowDetailedPlayerInfo; } set { } }
        public static bool DrawBestFitness { get { return !ShowNothing && Settings.ShowBestFitness; } set { } }
        public static bool ShowNothing = false;

        public static bool ShowBest = false;
        public static bool RunBest = false;
        public static bool RunThroughSpecies = false;
        public static int UpToSpecies = 0;
        public static bool ShowBestEachGen = false;
        public static int UpToGen = 0;

        public static CelestePlayer SpeciesChamp;
        public static CelestePlayer GenPlayerTemp;

        public static int FastForwardRate = 20; // How many updates to attempt to run per second
        public static int FrameLoops = 1;

        public static bool SkipBaseUpdate = false;

        private static State state = State.None;
        [Flags]
        private enum State
        {
            None = 0,
            Running = 1,
            Disabled = 2
        }
        private static KeyboardState kbState; // For handling the bot enabling/disabling (state changes)
        public static InputPlayer inputPlayer;

        private static bool IsKeyDown(Keys key)
        {
            return kbState.IsKeyDown(key);
        }

        public CelesteBotInteropModule()
        {
            Instance = this;
        }

        public override void Load()
        {
            On.Monocle.Engine.Draw += Engine_Draw;
            On.Monocle.Engine.Update += Engine_Update;
            On.Monocle.MInput.Update += MInput_Update;
            On.Celeste.Celeste.OnSceneTransition += OnScene_Transition;

            orig_Game_Update = (h_Game_Update = new Detour(
                typeof(Game).GetMethod("Update", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
                typeof(CelesteBotInteropModule).GetMethod("Game_Update")
            )).GenerateTrampoline<d_Game_Update>();
            Logger.Log(ModLogKey, "Load successful");
        }

        public static Detour h_Game_Update;
        public delegate void d_Game_Update(Game self, GameTime gameTime);
        public static d_Game_Update orig_Game_Update;
        public static void Game_Update(Game self, GameTime gameTime)
        {
            if (Settings.Enabled && SkipBaseUpdate)
            {
                return;
            }

            orig_Game_Update(self, gameTime);
        }

        public override void Initialize()
        {
            base.Initialize();

            // Hey, InputPlayer should be made to work without removing self when players die
            inputPlayer = new InputPlayer(Celeste.Celeste.Instance, new InputData()); // Blank InputData when constructing. Overwrite it when needing to update inputs
            Celeste.Celeste.Instance.Components.Add(inputPlayer);
            CelesteBotManager.FillOrganismHash(CelesteBotManager.ORGANISM_PATH);
            CelesteBotManager.FillSpeciesHash(CelesteBotManager.SPECIES_PATH);
            population = new Population(CelesteBotManager.POPULATION_SIZE);
            //GeneratePlayer();
            CurrentPlayer = population.GetCurrentPlayer();
            
        }
        //public static void GeneratePlayer()
        //{
        //    CurrentPlayer = new CelestePlayer();
        //    CurrentPlayer.Brain.GenerateNetwork();
        //    CurrentPlayer.Brain.Mutate(innovationHistory);
        //}
        public override void Unload()
        {
            h_Game_Update.Undo();
            h_Game_Update.Free();
            On.Monocle.Engine.Draw -= Engine_Draw;
            On.Monocle.Engine.Update -= Engine_Update;
            On.Monocle.MInput.Update -= MInput_Update;
            On.Celeste.Celeste.OnSceneTransition -= OnScene_Transition;
            h_Game_Update.Undo();
            h_Game_Update.Free();
            Logger.Log(ModLogKey, "Unload successful");
        }

        public static void Engine_Draw(On.Monocle.Engine.orig_Draw original, Engine self, GameTime time)
        {
            original(self, time);
            if (state == State.Running || Settings.DrawAlways) {
                CelesteBotManager.Draw();
            }
        }

        private static void Reset(InputData temp)
        {
            temp.QuickRestart = true;
            buffer = CelesteBotManager.PLAYER_GRACE_BUFFER; // sets the buffer to desired wait time... magic
            inputPlayer.UpdateData(temp);
        }
        
        public static void MInput_Update(On.Monocle.MInput.orig_Update original)
        {
            if (!Settings.Enabled)
            {
                FrameLoops = 1;
                original();
                return;
            }
            if (Settings.FastForward)
            {
                HandleFrameRates();
            }
            if (CelesteBotManager.CompleteRestart(inputPlayer))
            {
                return;
            }
            if (CelesteBotManager.CheckForCutsceneSkip(inputPlayer))
            {
                return;
            }
            if (CelesteBotManager.CompleteCutsceneSkip(inputPlayer))
            {
                return;
            }// test
            
            InputData temp = new InputData();
            
            // If in cutscene skip state, skip it the rest of the way.
            kbState = Keyboard.GetState();
            // Make replaying a thing that happens now
            if (IsKeyDown(Keys.Space))
            {
                ShowBest = !ShowBest;
            } else if (IsKeyDown(Keys.B))
            {
                RunBest = !RunBest;
                Reset(temp);
                return;
            } else if (IsKeyDown(Keys.S))
            {
                RunThroughSpecies = !RunThroughSpecies;
                UpToSpecies = 0;
                Species s = (Species)population.Species[0];
                CelestePlayer p = (CelestePlayer)s.Champ;
                SpeciesChamp = p.CloneForReplay();
                Reset(temp);
                return;
            } else if (IsKeyDown(Keys.G))
            {
                ShowBestEachGen = !ShowBestEachGen;
                UpToGen = 0;
                CelestePlayer p = (CelestePlayer)population.GenPlayers[0];
                GenPlayerTemp = p.CloneForReplay();
                Reset(temp);
                return;
            } else if (IsKeyDown(Keys.OemBackslash))
            {
                state = State.Running;
            } else if (IsKeyDown(Keys.OemQuotes))
            {
                state = State.Running;
            } else if (IsKeyDown(Keys.OemPeriod))
            {
                state = State.Disabled;
                temp.QuickRestart = true;
            } else if (IsKeyDown(Keys.OemComma))
            {
                state = State.Disabled;
                temp.ESC = true;
            } else if (IsKeyDown(Keys.OemQuestion))
            {
                state = State.Disabled;
                //GeneratePlayer();
            } else if (IsKeyDown(Keys.N))
            {
                ShowNothing = !ShowNothing;
            }
            if (state == State.Running)
            {
                if (buffer > 0)
                {
                    buffer--;
                    original();
                    inputPlayer.UpdateData(temp);
                    return;
                }
                if (ShowBestEachGen)
                {
                    if (!GenPlayerTemp.Dead)
                    {
                        CurrentPlayer = GenPlayerTemp;
                        GenPlayerTemp.Update();
                        if (GenPlayerTemp.Dead)
                        {
                            Reset(temp);
                            UpToGen++;
                            if (UpToGen >= population.GenPlayers.Count)
                            {
                                UpToGen = 0;
                                ShowBestEachGen = false;
                            }
                            else
                            {
                                GenPlayerTemp = (CelestePlayer)population.GenPlayers[UpToGen];
                            }
                        }
                    }
                    else
                    {
                        Reset(temp);
                        UpToGen = 0;
                        ShowBestEachGen = false;
                    }
                    original();
                    return;
                }
                else if (RunThroughSpecies)
                {
                    if (!SpeciesChamp.Dead)
                    {
                        CurrentPlayer = SpeciesChamp;
                        SpeciesChamp.Update();
                        if (SpeciesChamp.Dead)
                        {
                            Reset(temp);
                            UpToSpecies++;
                            if (UpToSpecies >= population.Species.Count)
                            {
                                UpToSpecies = 0;
                                RunThroughSpecies = false;
                            }
                            else
                            {
                                Species s = (Species)population.Species[UpToSpecies];
                                SpeciesChamp = s.Champ.CloneForReplay();
                            }
                        }
                    }
                    else
                    {
                        Reset(temp);
                        UpToSpecies = 0;
                        RunThroughSpecies = false;
                    }
                    original();
                    return;
                }
                else if (RunBest)
                {
                    if (!population.BestPlayer.Dead)
                    {
                        CurrentPlayer = population.BestPlayer;
                        population.BestPlayer.Update();
                        if (population.BestPlayer.Dead)
                        {
                            Reset(temp);
                            RunBest = false;
                            population.BestPlayer = population.BestPlayer.CloneForReplay();
                        }
                    }
                    else
                    {
                        Reset(temp);
                        RunBest = false;
                        population.BestPlayer = population.BestPlayer.CloneForReplay();
                    }
                    original();
                    return;
                }
                else
                {
                    if (!population.Done())
                    {

                        // Run the population till they die
                        population.UpdateAlive();
                        CurrentPlayer = population.GetCurrentPlayer();
                        if (CurrentPlayer.Dead)
                        {
                            temp.QuickRestart = true;
                            buffer = CelesteBotManager.PLAYER_GRACE_BUFFER; // sets the buffer to desired wait time... magic
                            if (CurrentPlayer.Fitness > population.BestFitness)
                            {
                                population.BestFitness = CurrentPlayer.Fitness;
                                population.BestPlayer = CurrentPlayer.CloneForReplay();
                            }
                            population.CurrentIndex++;
                            if (population.CurrentIndex >= population.Pop.Count)
                            {
                                Logger.Log(CelesteBotInteropModule.ModLogKey, "Population Current Index out of bounds, performing evolution...");
                                //inputPlayer.UpdateData(temp);
                                //original();
                                //return;
                            }
                            inputPlayer.UpdateData(temp);
                        }
                        original();
                        return;
                    }
                    else
                    {
                        // Do some checkpointing here maybe
                        population.NaturalSelection();
                    }
                }
            }
            inputPlayer.UpdateData(temp);
            original();
        }
        public static void Engine_Update(On.Monocle.Engine.orig_Update original, Engine self, GameTime gameTime)
        {
            SkipBaseUpdate = false;

            if (!Settings.Enabled)
            {
                original(self, gameTime);
                return;
            }

            // The original patch doesn't store FrameLoops in a local variable, but it's only updated in UpdateInputs anyway.
            int loops = FrameLoops;
            if (state == State.Running)
            {
                if (loops > 10)
                {
                    // Loop without base.Update(), then call base.Update() once.
                    SkipBaseUpdate = true;
                    for (int i = 0; i < loops; i++)
                    {
                        original(self, gameTime);
                    }
                    SkipBaseUpdate = false;
                    // This _should_ work...
                    original(self, gameTime);
                    return;
                }

                loops = Math.Min(10, loops);
                for (int i = 0; i < loops; i++)
                {
                    original(self, gameTime);
                }
            } else
            {
                original(self, gameTime);
            }
        }
        private static void HandleFrameRates()
        {
            if (state == State.Running)
            {
                if (FrameLoops <= 1) {
                    FrameLoops = FastForwardRate;
                    return;
                }
            }
            else
            {
                FrameLoops = 1;
            }
        }
        public static void OnScene_Transition(On.Celeste.Celeste.orig_OnSceneTransition original, Celeste.Celeste self, Scene last, Scene next)
        {
            original(self, last, next);
            CurrentPlayer.SetupVision();
            TileFinder.GetAllEntities();
        }
    }
}