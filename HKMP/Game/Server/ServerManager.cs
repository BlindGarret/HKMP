﻿using System.Collections.Generic;
using System.Diagnostics;
using HKMP.Networking;
using HKMP.Networking.Packet;
using HKMP.Networking.Packet.Custom;
using HKMP.Networking.Server;
using HKMP.Util;
using Modding;
using UnityEngine;

namespace HKMP.Game.Server {
    /**
     * Class that manages the server state (similar to ClientManager).
     * For example the current scene of each player, to prevent sending redundant traffic.
     */
    public class ServerManager {
        // TODO: decide whether it is better to always transmit entire PlayerData objects instead of
        // multiple packets (one for position, one for scale, one for animation, etc.)
        private const int ConnectionTimeout = 3000;
        private const int HeartBeatInterval = 100;

        private readonly NetServer _netServer;

        private readonly Game.Settings.GameSettings _gameSettings;

        private readonly Dictionary<int, PlayerData> _playerData;
        
        private readonly Stopwatch _heartBeatSendStopwatch;

        public ServerManager(NetworkManager networkManager, Game.Settings.GameSettings gameSettings, PacketManager packetManager) {
            _netServer = networkManager.GetNetServer();
            _gameSettings = gameSettings;

            _playerData = new Dictionary<int, PlayerData>();

            _heartBeatSendStopwatch = new Stopwatch();
            
            // Register packet handlers
            packetManager.RegisterServerPacketHandler<HelloServerPacket>(PacketId.HelloServer, OnHelloServer);
            packetManager.RegisterServerPacketHandler<PlayerChangeScenePacket>(PacketId.PlayerChangeScene, OnClientChangeScene);
            packetManager.RegisterServerPacketHandler<ServerPlayerUpdatePacket>(PacketId.PlayerUpdate, OnPlayerUpdate);
            packetManager.RegisterServerPacketHandler<ServerPlayerAnimationUpdatePacket>(PacketId.PlayerAnimationUpdate, OnPlayerUpdateAnimation);
            packetManager.RegisterServerPacketHandler<PlayerDisconnectPacket>(PacketId.PlayerDisconnect, OnPlayerDisconnect);
            packetManager.RegisterServerPacketHandler<ServerPlayerDeathPacket>(PacketId.PlayerDeath, OnPlayerDeath);
            packetManager.RegisterServerPacketHandler<ServerHeartBeatPacket>(PacketId.HeartBeat, OnHeartBeat);
            packetManager.RegisterServerPacketHandler<ServerDreamshieldSpawnPacket>(PacketId.DreamshieldSpawn, OnDreamshieldSpawn);
            packetManager.RegisterServerPacketHandler<ServerDreamshieldDespawnPacket>(PacketId.DreamshieldDespawn, OnDreamshieldDespawn);
            packetManager.RegisterServerPacketHandler<ServerDreamshieldUpdatePacket>(PacketId.DreamshieldUpdate, OnDreamshieldUpdate);
            
            // Register server shutdown handler
            _netServer.RegisterOnShutdown(OnServerShutdown);
            
            // Register application quit handler
            ModHooks.Instance.ApplicationQuitHook += OnApplicationQuit;
        }

        /**
         * Starts a server with the given port
         */
        public void Start(int port) {
            // Stop existing server
            if (_netServer.IsStarted) {
                Logger.Warn(this, "Server was running, shutting it down before starting");
                _netServer.Stop();
            }

            // Start server again with given port
            _netServer.Start(port);

            _heartBeatSendStopwatch.Reset();
            _heartBeatSendStopwatch.Start();
            
            MonoBehaviourUtil.Instance.OnUpdateEvent += CheckHeartBeat;
        }

        /**
         * Stops the currently running server
         */
        public void Stop() {
            if (_netServer.IsStarted) {
                // Before shutting down, send TCP packets to all clients indicating
                // that the server is shutting down
                _netServer.BroadcastTcp(new ServerShutdownPacket().CreatePacket());
                
                _netServer.Stop();
            } else {
                Logger.Warn(this, "Could not stop server, it was not started");
            }
        }

        /**
         * Called when the game settings are updated, and need to be broadcast
         */
        public void OnUpdateGameSettings() {
            if (!_netServer.IsStarted) {
                return;
            }
        
            var settingsUpdatePacket = new GameSettingsUpdatePacket {
                GameSettings = _gameSettings
            };
            settingsUpdatePacket.CreatePacket();
            
            _netServer.BroadcastTcp(settingsUpdatePacket);
        }

        private void OnHelloServer(int id, HelloServerPacket packet) {
            Logger.Info(this, $"Received Hello packet from ID {id}");
            
            // Start by sending the new client the current Server Settings
            var settingsUpdatePacket = new GameSettingsUpdatePacket {
                GameSettings = _gameSettings
            };
            settingsUpdatePacket.CreatePacket();
            
            _netServer.SendTcp(id, settingsUpdatePacket);
            
            // Read username from packet
            var username = packet.Username;

            // Read scene name from packet
            var sceneName = packet.SceneName;
            
            // Read the rest of the data, since we know that we have it
            var position = packet.Position;
            var scale = packet.Scale;
            var currentClip = packet.AnimationClipName;
            
            // Create new player data object
            var playerData = new PlayerData(
                username,
                sceneName,
                position,
                scale,
                currentClip
            );
            // Store data in mapping
            _playerData[id] = playerData;

            // Create PlayerEnterScene packet
            var enterScenePacket = new PlayerEnterScenePacket {
                Id = id,
                Username = username,
                Position = position,
                Scale = scale,
                AnimationClipName = currentClip
            };
            enterScenePacket.CreatePacket();
            
            // Send the packets to all clients in the same scene except the source client
            foreach (var idPlayerDataPair in _playerData) {
                if (idPlayerDataPair.Key == id) {
                    continue;
                }

                var otherPlayerData = idPlayerDataPair.Value;
                if (otherPlayerData.CurrentScene.Equals(sceneName)) {
                    _netServer.SendTcp(idPlayerDataPair.Key, enterScenePacket);
                    
                    // Also send the source client a packet that this player is in their scene
                    var alreadyInScenePacket = new PlayerEnterScenePacket {
                        Id = idPlayerDataPair.Key,
                        Username = otherPlayerData.Name,
                        Position = otherPlayerData.LastPosition,
                        Scale = otherPlayerData.LastScale,
                        AnimationClipName = otherPlayerData.LastAnimationClip
                    };
                    alreadyInScenePacket.CreatePacket();
                    
                    _netServer.SendTcp(id, alreadyInScenePacket);
                }
                
                // Send the source client a map update packet of the last location of the other players
                // var mapUpdatePacket = new ClientPlayerMapUpdatePacket {
                //     Id = idPlayerDataPair.Key,
                //     Position = otherPlayerData.LastMapLocation
                // };
                // mapUpdatePacket.CreatePacket();
                //
                // _netServer.SendUdp(id, mapUpdatePacket);
            }
        }
        
        private void OnClientChangeScene(int id, PlayerChangeScenePacket packet) {
            // Initialize with default value, override if mapping has key
            var oldSceneName = "NonGameplay";
            if (_playerData.ContainsKey(id)) {
                oldSceneName = _playerData[id].CurrentScene;                
            }

            var newSceneName = packet.NewSceneName;
            
            // Check whether the scene has changed, it might not change if
            // a player died and respawned in the same scene
            if (oldSceneName.Equals(newSceneName)) {
                Logger.Warn(this, $"Received SceneChange packet from ID {id}, from and to {oldSceneName}, probably a Death event");
            } else {
                Logger.Info(this, $"Received SceneChange packet from ID {id}, from {oldSceneName} to {newSceneName}");
            }

            // Read the position and scale in the new scene
            var position = packet.Position;
            var scale = packet.Scale;
            var animationClipName = packet.AnimationClipName;
            
            // Store it in their PlayerData object
            var playerData = _playerData[id];
            playerData.CurrentScene = newSceneName;
            playerData.LastPosition = position;
            playerData.LastScale = scale;
            playerData.LastAnimationClip = animationClipName;
            
            // Create packets in advance
            // Create a PlayerLeaveScene packet containing the ID
            // of the player leaving the scene
            var leaveScenePacket = new PlayerLeaveScenePacket {
                Id = id
            };
            leaveScenePacket.CreatePacket();
            
            // Create a PlayerEnterScene packet containing the ID
            // of the player entering the scene and their position
            var enterScenePacket = new PlayerEnterScenePacket {
                Id = id,
                Username = playerData.Name,
                Position = position,
                Scale = scale,
                AnimationClipName = animationClipName
            };
            enterScenePacket.CreatePacket();
            
            foreach (var idPlayerDataPair in _playerData) {
                // Skip source player
                if (idPlayerDataPair.Key == id) {
                    continue;
                }

                var otherPlayerData = idPlayerDataPair.Value;
                
                // Send the packet to all clients on the old scene
                // to indicate that this client has left their scene
                if (otherPlayerData.CurrentScene.Equals(oldSceneName)) {
                    Logger.Info(this, $"Sending leave scene packet to {idPlayerDataPair.Key}");
                    _netServer.SendTcp(idPlayerDataPair.Key, leaveScenePacket);
                }
                
                // Send the packet to all clients on the new scene
                // to indicate that this client has entered their scene
                if (otherPlayerData.CurrentScene.Equals(newSceneName)) {
                    Logger.Info(this, $"Sending enter scene packet to {idPlayerDataPair.Key}");
                    _netServer.SendTcp(idPlayerDataPair.Key, enterScenePacket);
                    
                    Logger.Info(this, $"Sending that {idPlayerDataPair.Key} is already in scene to {id}");
                    
                    // Also send a packet to the client that switched scenes,
                    // notifying that these players are already in this new scene
                    var alreadyInScenePacket = new PlayerEnterScenePacket {
                        Id = idPlayerDataPair.Key,
                        Username = otherPlayerData.Name,
                        Position = otherPlayerData.LastPosition,
                        Scale = otherPlayerData.LastScale,
                        AnimationClipName = otherPlayerData.LastAnimationClip
                    };
                    alreadyInScenePacket.CreatePacket();
                    
                    _netServer.SendTcp(id, alreadyInScenePacket);
                }
            }
            
            // Store the new PlayerData object in the mapping
            _playerData[id] = playerData;
        }

        private void OnPlayerUpdate(int id, ServerPlayerUpdatePacket packet) {
            if (!_playerData.ContainsKey(id)) {
                Logger.Warn(this, $"Received PlayerPositionUpdate packet, but player with ID {id} is not in mapping");
                return;
            }

            // Get current scene of player
            var currentScene = _playerData[id].CurrentScene;

            var position = packet.Position;
            var scale = packet.Scale;
            var mapPosition = packet.MapPosition;
            
            // Store the new position in the last position mapping
            _playerData[id].LastPosition = position;
            _playerData[id].LastScale = scale;
            _playerData[id].LastMapLocation = mapPosition;

            // Create the packet in advance
            var positionUpdatePacket = new ClientPlayerUpdatePacket {
                Id = (ushort) id,
                Position = position,
                Scale = scale,
                MapPosition = mapPosition
            };
            positionUpdatePacket.CreatePacket();

            // Send the packet to all clients in the same scene
            SendPacketToClientsInSameScene(positionUpdatePacket, false, currentScene, id);
        }

        private void OnPlayerUpdateAnimation(int id, ServerPlayerAnimationUpdatePacket packet) {
            if (!_playerData.ContainsKey(id)) {
                Logger.Warn(this, $"Received PlayerAnimationUpdate packet, but player with ID {id} is not in mapping");
                return;
            }
            
            // Get current scene of player
            var currentScene = _playerData[id].CurrentScene;
            
            // Get the clip name from the packet
            var clipName = packet.AnimationClipName;
            
            // Get the frame from the packet
            var frame = packet.Frame;
            
            // Get the boolean list of effect info
            var effectInfo = packet.EffectInfo;

            // Store the new animation in the player data
            _playerData[id].LastAnimationClip = clipName;
            
            // Create the packet in advance
            var animationUpdatePacket = new ClientPlayerAnimationUpdatePacket {
                Id = id,
                ClipName = clipName,
                Frame = frame,
                
                EffectInfo = effectInfo
            };
            animationUpdatePacket.CreatePacket();

            // Send the packet to all clients in the same scene
            SendPacketToClientsInSameScene(animationUpdatePacket, false, currentScene, id);
        }

        private void OnPlayerDisconnect(int id, Packet packet) {
            Logger.Info(this, $"Received Disconnect packet from ID {id}");
            OnPlayerDisconnect(id);
        }

        private void OnPlayerDisconnect(int id) {
            // Always propagate this packet to the NetServer
            _netServer.OnClientDisconnect(id);

            if (!_playerData.ContainsKey(id)) {
                Logger.Warn(this, $"Player disconnect, but player with ID {id} is not in mapping");
                return;
            }

            // Get the scene that client was in while disconnecting
            var currentScene = _playerData[id].CurrentScene;

            // Create a PlayerLeaveScene packet containing the ID
            // of the player disconnecting
            var leaveScenePacket = new PlayerLeaveScenePacket {
                Id = id
            };
            leaveScenePacket.CreatePacket();

            // Send the packet to all clients in the same scene
            SendPacketToClientsInSameScene(leaveScenePacket, true, currentScene, id);
            
            // // Also create a MapUpdate packet containing the ID
            // // of the player disconnecting and an empty location
            // var mapUpdatePacket = new ClientPlayerMapUpdatePacket {
            //     Id = id,
            //     Position = Vector3.zero
            // };
            // mapUpdatePacket.CreatePacket();
            //
            // // We might as well broadcast this over TCP as it doesn't happen often and does not require speed
            // _netServer.BroadcastTcp(mapUpdatePacket);

            // Now remove the client from the player data mapping
            _playerData.Remove(id);
        }

        private void OnPlayerDeath(int id, Packet packet) {
            if (!_playerData.ContainsKey(id)) {
                Logger.Warn(this, $"Received PlayerDeath packet, but player with ID {id} is not in mapping");
                return;
            }

            Logger.Info(this, $"Received PlayerDeath packet from ID {id}");
            
            // Get the scene that the client was last in
            var currentScene = _playerData[id].CurrentScene;
            
            // Create a new PlayerDeath packet containing the ID of the player that died
            var playerDeathPacket = new ClientPlayerDeathPacket {
                Id = id
            };
            playerDeathPacket.CreatePacket();
            
            // Send the packet to all clients in the same scene
            SendPacketToClientsInSameScene(playerDeathPacket, true, currentScene, id);
        }
        
        private void OnDreamshieldSpawn(int id, ServerDreamshieldSpawnPacket packet) {
            if (!_playerData.ContainsKey(id)) {
                Logger.Warn(this, $"Received DreamshieldSpawn packet, but player with ID {id} is not in mapping");
                return;
            }

            Logger.Info(this, $"Received DreamshieldSpawn packet from ID {id}");
            
            // Get the scene that the client was last in
            var currentScene = _playerData[id].CurrentScene;
            
            // Create a new DreamshieldSpawn packet containing the ID of the player
            var dreamshieldSpawnPacket = new ClientDreamshieldSpawnPacket {
                Id = id
            };
            dreamshieldSpawnPacket.CreatePacket();
            
            // Send the packet to all clients in the same scene
            SendPacketToClientsInSameScene(dreamshieldSpawnPacket, false, currentScene, id);
        }
        
        private void OnDreamshieldDespawn(int id, ServerDreamshieldDespawnPacket packet) {
            if (!_playerData.ContainsKey(id)) {
                Logger.Warn(this, $"Received DreamshieldDespawn packet, but player with ID {id} is not in mapping");
                return;
            }

            Logger.Info(this, $"Received DreamshieldDespawn packet from ID {id}");
            
            // Get the scene that the client was last in
            var currentScene = _playerData[id].CurrentScene;
            
            // Create a new DreamshieldDespawn packet containing the ID of the player
            var dreamshieldDespawnPacket = new ClientDreamshieldDespawnPacket {
                Id = id
            };
            dreamshieldDespawnPacket.CreatePacket();
            
            // Send the packet to all clients in the same scene
            SendPacketToClientsInSameScene(dreamshieldDespawnPacket, false, currentScene, id);
        }

        private void OnDreamshieldUpdate(int id, ServerDreamshieldUpdatePacket packet) {
            if (!_playerData.ContainsKey(id)) {
                Logger.Warn(this, $"Received DreamshieldUpdate packet, but player with ID {id} is not in mapping");
                return;
            }

            // Get the scene that the client was last in
            var currentScene = _playerData[id].CurrentScene;
            
            // Create a new DreamshieldDespawn packet containing the ID of the player
            var dreamshieldUpdatePacket = new ClientDreamshieldUpdatePacket {
                Id = id,
                BlockEffect = packet.BlockEffect,
                BreakEffect = packet.BreakEffect,
                ReformEffect = packet.ReformEffect
            };
            dreamshieldUpdatePacket.CreatePacket();
            
            // Send the packet to all clients in the same scene
            SendPacketToClientsInSameScene(dreamshieldUpdatePacket, false, currentScene, id);
        }

        private void OnServerShutdown() {
            // Clear all existing player data
            _playerData.Clear();
            
            // De-register the heart beat update
            MonoBehaviourUtil.Instance.OnUpdateEvent -= CheckHeartBeat;
            
            // Stop sending heart beats
            _heartBeatSendStopwatch.Stop();
        }

        private void OnApplicationQuit() {
            Stop();
        }
        
        private void CheckHeartBeat() {
            // The server is not started, so there is no need to check heart beats
            if (!_netServer.IsStarted) {
                return;
            }

            // For each connected client, check whether a heart beat has been received recently
            // foreach (var idPlayerDataPair in _playerData) {
            //     if (idPlayerDataPair.Value.HeartBeatStopwatch.ElapsedMilliseconds > ConnectionTimeout) {
            //         // The stopwatch has surpassed the connection timeout value, so we disconnect the client
            //         var id = idPlayerDataPair.Key;
            //         Logger.Info(this, 
            //             $"Didn't receive heart beat from player {id} in {ConnectionTimeout} milliseconds, dropping client");
            //         OnPlayerDisconnect(id);
            //     }                
            // }
            //
            // // If it is time to send another heart beat to the clients
            // if (_heartBeatSendStopwatch.ElapsedMilliseconds > HeartBeatInterval) {
            //     // Create and broadcast the heart beat over UDP
            //     _netServer.BroadcastUdp(new ClientHeartBeatPacket().CreatePacket());
            //
            //     // And reset the timer, so we know when to send the next
            //     _heartBeatSendStopwatch.Reset();
            //     _heartBeatSendStopwatch.Start();
            // }
        }
        
        private void OnHeartBeat(int id, ServerHeartBeatPacket packet) {
            if (!_playerData.ContainsKey(id)) {
                Logger.Info(this, $"Received heart beat from unknown client with ID {id}");
                return;
            }

            // We received a heart beat from this ID, so we reset their stopwatch
            var stopwatch = _playerData[id].HeartBeatStopwatch;
            stopwatch.Reset();
            stopwatch.Start();
        }

        private void SendPacketToClientsInSameScene(Packet packet, bool tcp, string targetScene, int excludeId) {
            foreach (var idScenePair in _playerData) {
                if (idScenePair.Key == excludeId) {
                    continue;
                }
                
                if (idScenePair.Value.CurrentScene.Equals(targetScene)) {
                    if (tcp) {
                        _netServer.SendTcp(idScenePair.Key, packet);   
                    } else {
                        _netServer.SendUdp(idScenePair.Key, packet);
                    }
                }
            }
        }

    }
}