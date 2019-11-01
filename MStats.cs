using System;
using System.Text;
using System.Net;
using Oxide.Core;
using Oxide.Core.Database;
using Oxide.Core.MySql;
using Oxide.Core.Plugins;
using Oxide.Core.Libraries;
using Oxide.Core.Configuration;
using System.Collections.Generic;
using UnityEngine;
using System.Linq;

namespace Oxide.Plugins {
    [Info("MStats", "Limmek", "1.3.1"/*, ResourceId = 0*/)]
    [Description("Logs player statistics and other server stuff to MySql")]
    
    public class MStats : RustPlugin { 	
    	private Dictionary<BasePlayer, Int32> loginTime = new Dictionary<BasePlayer, int>();
    	//private readonly Core.MySql.Libraries.MySql _mySql = Interface.GetMod().GetLibrary<Core.MySql.Libraries.MySql>();
		readonly Core.MySql.Libraries.MySql _mySql = new Core.MySql.Libraries.MySql();
        private Connection _mySqlConnection = null;

		//Config
		protected override void LoadDefaultConfig() {
            PrintWarning("Creating a new configuration file");
            Config.Clear();
            Config["Host"] = "127.0.0.1";
            Config["Database"] = "database";
            Config["Port"] = 3306;
            Config["Username"] = "username";
            Config["Password"] = "password";
            Config["_MySQL"] = false;
            Config["_LogChat"] = false;
            Config["_LogConsole"] = false; 
            Config["_LogAridrops"] = false;
            Config["_AdminLog"] = false;
            Config["_AdminLogWords"] = "admin, admn, kim, rasist, fuck";
            Config["Version"] = "1.3.1";     
            SaveConfig();
        }

        // MySQL Connection
        private void StartConnection() {
        	try {
        		Puts("Opening connection.");
	            if (usingMySQL() && _mySqlConnection == null) {
	                _mySqlConnection = _mySql.OpenDb(Config["Host"].ToString(), Convert.ToInt32(Config["Port"]), Config["Database"].ToString(), Config["Username"].ToString(), Config["Password"].ToString(), this);
	                Puts("Connection opened.");
        		}

        	}
        	catch (Exception ex)
        	{
        		Puts(ex.Message);
        	}
        }
        
        // Execute query
        public void executeQuery(string query, params object[] data) {
            var sql = Sql.Builder.Append(query, data);
            _mySql.Insert(sql, _mySqlConnection);
        }

        private void createTablesOnConnect() {	
			try {
	        	//PrintWarning("Creating tables...");
            	//executeQuery("CREATE DATABASE IF NOT EXISTS mstats");
            	executeQuery("CREATE TABLE IF NOT EXISTS player_stats 			(id INT(11) NOT NULL AUTO_INCREMENT, player_id BIGINT(20) NULL, player_name VARCHAR(255) NULL, player_ip VARCHAR(128) NULL, player_state INT(1) NULL DEFAULT '0', player_online_time BIGINT(20) DEFAULT '0', player_last_login TIMESTAMP NULL, PRIMARY KEY (`id`), UNIQUE (`player_id`) ) ENGINE=InnoDB;");   
            	executeQuery("CREATE TABLE IF NOT EXISTS player_resource_gather (id INT(11) NOT NULL AUTO_INCREMENT, player_id BIGINT(20) NULL, player_name VARCHAR(255) NULL, resource VARCHAR(255) NULL, amount INT(32), date DATE NULL, PRIMARY KEY (`id`), UNIQUE KEY `PlayerGather` (`player_id`,`resource`,`date`) ) ENGINE=InnoDB;");
            	executeQuery("CREATE TABLE IF NOT EXISTS player_crafted_item 	(id INT(11) NOT NULL AUTO_INCREMENT, player_id BIGINT(20) NULL, item VARCHAR(128), amount INT(32), date DATE NULL, PRIMARY KEY (`id`), UNIQUE KEY `PlayerItem` (`player_id`,`item`,`date`) ) ENGINE=InnoDB;");
            	executeQuery("CREATE TABLE IF NOT EXISTS player_bullets_fired	(id INT(11) NOT NULL AUTO_INCREMENT, player_id BIGINT(20) NULL, bullet_name VARCHAR(128) NULL, bullets_fired INT(32) DEFAULT '1', weapon_name VARCHAR(128) NULL, date DATE NULL, PRIMARY KEY (`id`), UNIQUE KEY `PlayerBullet` (`player_id`,`bullet_name`,`weapon_name`,`date`) ) ENGINE=InnoDB;");
            	executeQuery("CREATE TABLE IF NOT EXISTS player_kill_animal		(id INT(11) NOT NULL AUTO_INCREMENT, player_id BIGINT(20) NULL, animal VARCHAR(128), distance INT(11) NULL DEFAULT '0', weapon VARCHAR(128) NULL, time TIMESTAMP NULL, PRIMARY KEY (`id`) ) ENGINE=InnoDB;");
            	executeQuery("CREATE TABLE IF NOT EXISTS player_kill			(id INT(11) NOT NULL AUTO_INCREMENT, killer_id BIGINT(20) NULL, victim_id BIGINT(20) NULL, bodypart VARCHAR(128), weapon VARCHAR(128), distance INT(11) NULL, time TIMESTAMP NULL, PRIMARY KEY (`id`) ) ENGINE=InnoDB;");
            	executeQuery("CREATE TABLE IF NOT EXISTS player_death			(id INT(11) NOT NULL AUTO_INCREMENT, player_id BIGINT(20) NULL, cause VARCHAR(128), count INT(11) NULL DEFAULT '1', date DATE NULL, time TIMESTAMP NULL, PRIMARY KEY (`id`), UNIQUE (`player_id`,`date`,`cause`) ) ENGINE=InnoDB;");
            	executeQuery("CREATE TABLE IF NOT EXISTS player_destroy_building(id INT(11) NOT NULL AUTO_INCREMENT, player_id BIGINT(20) NULL, building VARCHAR(128), building_grade VARCHAR(128), weapon VARCHAR(128), time TIMESTAMP NULL, PRIMARY KEY (`id`) ) ENGINE=InnoDB;");
            	executeQuery("CREATE TABLE IF NOT EXISTS player_place_building 	(id INT(11) NOT NULL AUTO_INCREMENT, player_id BIGINT(20) NULL, player_name VARCHAR(128) NULL, building VARCHAR(128) NULL, amount INT(32) NULL DEFAULT '1', date DATE NULL, PRIMARY KEY (`id`), UNIQUE (`player_id`,`date`,`building`) ) ENGINE=InnoDB;");
            	executeQuery("CREATE TABLE IF NOT EXISTS player_place_deployable(id INT(11) NOT NULL AUTO_INCREMENT, player_id BIGINT(20) NULL, player_name VARCHAR(128) NULL, deployable VARCHAR(128) NULL, amount INT(32) NULL DEFAULT '1', date DATE NULL, PRIMARY KEY (`id`), UNIQUE (`player_id`,`date`,`deployable`) ) ENGINE=InnoDB;");
				executeQuery("CREATE TABLE IF NOT EXISTS player_authorize_list	(id INT(11) NOT NULL AUTO_INCREMENT, player_id BIGINT(20) NULL, player_name VARCHAR(128) NULL, cupboard VARCHAR(128) NULL, access INT(32) NULL DEFAULT '0', time TIMESTAMP NULL, PRIMARY KEY (`id`), UNIQUE (`Cupboard`) ) ENGINE=InnoDB;");
				executeQuery("CREATE TABLE IF NOT EXISTS player_chat_command	(id INT(11) NOT NULL AUTO_INCREMENT, player_id BIGINT(20) NULL, player_name VARCHAR(128) NULL, command VARCHAR(128) NULL, text VARCHAR(255) NULL, time TIMESTAMP NULL, PRIMARY KEY (`id`) ) ENGINE=InnoDB;");
				executeQuery("CREATE TABLE IF NOT EXISTS player_connect_log		(id INT(11) NOT NULL AUTO_INCREMENT, player_id BIGINT(20) NULL, player_name VARCHAR(128) NULL, state VARCHAR(128) NULL, time TIMESTAMP NULL, PRIMARY KEY (`id`) ) ENGINE=InnoDB;");
				if (LogChat() == true) {
					executeQuery("CREATE TABLE IF NOT EXISTS server_log_chat	(id INT(11) NOT NULL AUTO_INCREMENT, player_id BIGINT(20) NULL, player_name VARCHAR(128) NULL, player_ip VARCHAR(128), chat_message VARCHAR(255), admin INT(32) NULL DEFAULT '0', time TIMESTAMP NULL, PRIMARY KEY (`id`) ) ENGINE=InnoDB;");
				}
				if (LogConsole() == true) {
					executeQuery("CREATE TABLE IF NOT EXISTS server_log_console (id INT(11) NOT NULL AUTO_INCREMENT, server_message VARCHAR(255) NULL, time TIMESTAMP NULL, PRIMARY KEY (`id`) ) ENGINE=InnoDB;");
				}
				if (LogAiredrop() == true) {
					executeQuery("CREATE TABLE IF NOT EXISTS server_log_airdrop	(id INT(11) NOT NULL AUTO_INCREMENT, plane VARCHAR(128) NULL, location VARCHAR(128) NULL, time TIMESTAMP NULL, PRIMARY KEY (`id`) ) ENGINE=InnoDB;");
				}
				if (LogAdminCall() == true) {
					executeQuery("CREATE TABLE IF NOT EXISTS admin_log	(id INT(11) NOT NULL AUTO_INCREMENT, player_id BIGINT(20) NULL, player_name VARCHAR(128) NULL, command VARCHAR(128) NULL, text VARCHAR(255) NULL, time TIMESTAMP NULL, PRIMARY KEY (`id`) ) ENGINE=InnoDB;");
				}
			}
            catch (Exception ex) {
                Puts(ex.ToString());
            }

        }

        void Init() {
            #if !RUST
            throw new NotSupportedException("This plugin does not support this game");
            #endif

       		string curVersion = Version.ToString();
            string[] version = curVersion.Split('.');
            var majorPluginUpdate = version[0]; 	// Big Plugin Update
            var minorPluginUpdate = version[1];		// Small Plugin Update
            var databaseVersion = version[2];		// Database Update
        	var pluginVersion = majorPluginUpdate+"."+minorPluginUpdate;
        	Puts("Plugin version: "+majorPluginUpdate+"."+minorPluginUpdate+"  Database version: "+databaseVersion);
        	if (pluginVersion != getConfigVersion("plugin") ) {
        		Puts("New "+pluginVersion+" Old "+getConfigVersion("plugin") );
        		Config["Version"] = pluginVersion+"."+databaseVersion;     
	            SaveConfig();	 
        	}
        	if (databaseVersion != getConfigVersion("db") ){
        		Puts("New "+databaseVersion+" Old "+getConfigVersion("db") );
        		PrintWarning("Database base changes please drop the old!");
        		Config["Version"] = pluginVersion+"."+databaseVersion;    
	            SaveConfig();	           		
        	}
            
	                
	       
		}   

        //Plugin loaded
        void Loaded() {
        	StartConnection();
        	createTablesOnConnect();
        	foreach (var player in BasePlayer.activePlayerList) {
                OnPlayerInit(player);
            }           
        }

        //Plugin unloaded
        void Unloaded() {
            foreach (var player in BasePlayer.activePlayerList)
            {
                OnPlayerDisconnected(player);
            }
            timer.Once(5, () =>
            {
                _mySql.CloseDb(_mySqlConnection);
                _mySqlConnection = null;
            });
        }


        /*********************************
        **         Player Hooks         **
        *********************************/

        //Player login
        void OnPlayerInit(BasePlayer player) {
            if (!player.IsConnected)
                return;

            string properName = EncodeNonAsciiCharacters(player.displayName);

            executeQuery(
                "INSERT INTO player_stats (player_id, player_name, player_ip, player_state, player_last_login) VALUES (@0, @1, @2, 1, @3) ON DUPLICATE KEY UPDATE player_name = @1, player_ip = @2, player_state = 1, player_last_login= @3",
                player.userID, properName, player.net.connection.ipaddress, getDateTime());
            if(loginTime.ContainsKey(player))
                OnPlayerDisconnected(player);

            loginTime.Add(player, (Int32) (DateTime.UtcNow.Subtract(new DateTime(1970, 1, 1))).TotalSeconds);
		    executeQuery("INSERT INTO player_connect_log (player_id, player_name, state, time) VALUES (@0, @1, @2, @3)",
            			 player.userID, EncodeNonAsciiCharacters(player.displayName),"Connected", getDateTime());
        }

		//Player Logout
		void OnPlayerDisconnected(BasePlayer player) {
			if(loginTime.ContainsKey(player)) {
		    	//Puts("OnPlayerDisconnected works!");
		    	executeQuery(
		    		"UPDATE player_stats SET player_online_time = player_online_time + @0, player_state = 0 WHERE player_id = @1",
		    		(Int32)(DateTime.UtcNow.Subtract(new DateTime(1970, 1, 1))).TotalSeconds - loginTime[player], player.userID);
				loginTime.Remove(player);
			}
		    executeQuery("INSERT INTO player_connect_log (player_id, player_name, state, time) VALUES (@0, @1, @2, @3)",
            			 player.userID, EncodeNonAsciiCharacters(player.displayName),"Disconnected", getDateTime());
		}

		//Player Gather resource
        void OnDispenserGather(ResourceDispenser dispenser, BaseEntity entity, Item item) {
            if(entity is BasePlayer) {		
            	string properName = EncodeNonAsciiCharacters(((BasePlayer)entity).displayName);
                executeQuery(
                	"INSERT INTO player_resource_gather (player_id, resource, amount, date, player_name) VALUES (@0, @1, @2, @3, @4)"+
               		"ON DUPLICATE KEY UPDATE amount = amount + "+item.amount, ((BasePlayer)entity).userID, item.info.displayName.english, item.amount, getDate(), properName);
            }
        }

        //Player Pickup resource
        void OnCollectiblePickup(Item item, BasePlayer player) {
        	string properName = EncodeNonAsciiCharacters(((BasePlayer)player).displayName);
            executeQuery(
            	"INSERT INTO player_resource_gather (player_id, resource, amount, date, player_name) VALUES (@0, @1, @2, @3, @4)"+
               	"ON DUPLICATE KEY UPDATE amount = amount +"+item.amount, ((BasePlayer)player).userID, item.info.displayName.english, item.amount, getDate(), properName);
        }

        //Player crafted item
        void OnItemCraftFinished(ItemCraftTask task, Item item) {
            executeQuery("INSERT INTO player_crafted_item (player_id, item, amount, date) VALUES (@0, @1, @2, @3)"+
               "ON DUPLICATE KEY UPDATE amount = amount +"+item.amount, task.owner.userID, item.info.displayName.english, item.amount, getDate() );
        }

        
        // Player place item or building
		void OnEntityBuilt(Planner plan, GameObject go, HeldEntity heldentity) {
			string name = plan.GetOwnerPlayer().displayName; //Playername
			ulong playerID = plan.GetOwnerPlayer().userID; //steam_id
			var placedObject = go.ToBaseEntity();
		    if (placedObject is BuildingBlock) {
                string item_name = ((BuildingBlock)placedObject).blockDefinition.info.name.english;
                //Puts(playerID + name + item_name + getDate());
                executeQuery("INSERT INTO player_place_building (player_id, player_name, building, date) VALUES (@0, @1, @2, @3)" +
                        "ON DUPLICATE KEY UPDATE amount = amount + 1", playerID, name, item_name, getDate() );
            }else if (plan.isTypeDeployable) {
            	string item_name = plan.GetOwnerItemDefinition().displayName.english;
            	Puts(playerID + name + item_name + getDate());
                executeQuery("INSERT INTO player_place_deployable (player_id, player_name, deployable, date) VALUES (@0, @1, @2, @3)" +
                        "ON DUPLICATE KEY UPDATE amount = amount + 1", playerID, name, item_name, getDate() );
            }
            if (plan.GetOwnerItemDefinition().shortname == "cupboard.tool")
            {
                var cupboard = go.GetComponent<BuildingPrivlidge>();
                BasePlayer player = plan.GetOwnerPlayer();
                OnCupboardAuthorize(cupboard, player);
                OnCupboardAuthorize(cupboard, player); // Dirty fix for set access to 1
            }

		}


        /*********************************
        ** Weapons and Amunation Hooks  **
        *********************************/

        //Grab bullets fired and weapon type
		void OnWeaponFired(BaseProjectile projectile, BasePlayer player, ItemModProjectile itemProjectile, ProtoBuf.ProjectileShoot projectiles) {
            string bullet = "Unknown", weapon = (player.GetActiveItem() != null ? player.GetActiveItem().info.displayName.english : "Unknown");
            try
            {
                bullet = projectile.primaryMagazine.ammoType.displayName.english;
            }
            catch (Exception ex)
            {
                Puts(ex.StackTrace);
            }	
            executeQuery("INSERT INTO player_bullets_fired (player_id, bullet_name, weapon_name, date) VALUES (@0, @1, @2, @3)" +
                        "ON DUPLICATE KEY UPDATE bullets_fired = bullets_fired + 1", player.userID, bullet, weapon, getDate());
        }

        // RocketLuancher
        void OnRocketLaunched(BasePlayer player, BaseEntity entity) {
            string rocketName = "Unknown Rocket";
            string prefab = entity.ToString().ToLower();
            if (prefab.StartsWith("rocket_basic"))
                rocketName = "Rocket";
            else if (prefab.StartsWith("rocket_fire"))
                rocketName = "Incendiary Rocket";
            else if(prefab.StartsWith("rocket_hv"))
                rocketName = "High Velocity Rocket";
            else if(prefab.StartsWith("rocket_smoke"))
                rocketName = "Smoke Rocket WIP!!!!";
            executeQuery("INSERT INTO player_bullets_fired (player_id, bullet_name, weapon_name, date) VALUES (@0, @1, @2, @3)" +
                        "ON DUPLICATE KEY UPDATE bullets_fired = bullets_fired + 1", player.userID, rocketName, player.GetActiveItem().info.displayName.english, getDate());
        }

        // Explosive
        void OnExplosiveThrown(BasePlayer player, BaseEntity entity) {
            executeQuery("INSERT INTO player_bullets_fired (player_id, bullet_name, weapon_name, date) VALUES (@0, @1, @2, @3)" +
                        "ON DUPLICATE KEY UPDATE bullets_fired = bullets_fired + 1", player.userID, player.GetActiveItem().info.displayName.english, player.GetActiveItem().info.displayName.english, getDate());
        }
        
        // On death
        void OnEntityDeath(BaseCombatEntity entity, HitInfo hitInfo) {
            if (entity.lastAttacker != null && entity.lastAttacker is BasePlayer) {
                if (entity is BuildingBlock) {
                    BasePlayer attacker = ((BasePlayer) entity.lastAttacker);
                    string weapon = "Unknown";
                    try {
                        weapon = attacker.GetActiveItem().info.displayName.english;
                    }
                    catch{}
                    try {
                        executeQuery("INSERT INTO player_destroy_building (player_id, building, building_grade, weapon, time) VALUES (@0, @1, @2, @3, @4)",
	                        ((BasePlayer) entity.lastAttacker).userID, ((BuildingBlock) entity).blockDefinition.info.name.english, 
	                        ((BuildingBlock) entity).currentGrade.gradeBase.name.ToUpper() + " (" +
	                        ((BuildingBlock) entity).MaxHealth() + ")", weapon, getDateTime() );	
                    }
                    catch (Exception ex) {
                        throw new Exception(ex.Message);
                    }
                }
                else if (entity is BaseNpc) {
                    try {
                        string weapon = "Unknown";
                        try {
                            weapon = ((BasePlayer)entity.lastAttacker).GetActiveItem().info.displayName.english;
                        }
                        catch { }
                        string distance = "-1";
                        if (hitInfo != null)
                            distance = GetDistance(entity, hitInfo.Initiator) ?? "0";
                        else {
                            weapon += "(BLEED TO DEATH)";
                            distance = GetDistance(entity, (BasePlayer) entity.lastAttacker) ?? "0";
                        }
                        executeQuery("INSERT INTO player_kill_animal (player_id, animal, distance, weapon, time) VALUES (@0, @1, @2, @3, @4)",
                            ((BasePlayer) entity.lastAttacker).userID, GetFormattedAnimal(entity.ToString()), distance , weapon, getDateTime());
                    }
                    catch (Exception ex) {
                        Puts(ex.Message);
                    }
                }
                else if (entity is BasePlayer && entity != entity.lastAttacker) {
                    try {
                        string weapon = "Unknown";
                        try {
                            weapon = ((BasePlayer)entity.lastAttacker).GetActiveItem().info.displayName.english;
                        }
                        catch { }
                        string distance = "-1";
                        if (hitInfo != null)
                            distance = GetDistance(entity, hitInfo.Initiator) ?? "0";
                        else {
                            weapon += "(BLEED TO DEATH)";
                            distance = GetDistance(entity, (BasePlayer) entity.lastAttacker) ?? "0";
                        }
                        executeQuery("INSERT INTO player_kill (killer_id, victim_id, bodypart, weapon, distance, time) VALUES (@0, @1, @2, @3, @4, @5)",
                            ((BasePlayer) entity.lastAttacker).userID, ((BasePlayer) entity).userID,formatBodyPartName(hitInfo), weapon, distance, getDateTime() );
                    }
                    catch (Exception ex) {
                        Puts(ex.Message);
                    }
                }
            }
            try {
                if (entity is BasePlayer) {
                    string cause = entity.lastDamage.ToString().ToUpper();
                    executeQuery("INSERT INTO player_death (player_id, cause, date, time) VALUES (@0, @1, @2, @3)" +
                                 "ON DUPLICATE KEY UPDATE count = count + 1", ((BasePlayer) entity).userID, cause, getDate(), getDateTime());
                }
            }
            catch (Exception ex) {
                Puts(ex.Message);
                throw new Exception(ex.Message);
            }
        }


        /*********************************
        **    Cupboard Authorize Lis    **
        *********************************/

        // Using Cupboard priviliges granted
		void OnCupboardAuthorize(BuildingPrivlidge privilege, BasePlayer player) {
		    var priv = privilege.ToString();
		    var pid = player.userID.ToString();
		    var pname = player.displayName.ToString();
		    PrintWarning(priv+" "+pid+" "+pname);
            executeQuery("INSERT INTO player_authorize_list (player_id, player_name, cupboard, time) VALUES (@0, @1, @2, @3)" +
                         "ON DUPLICATE KEY UPDATE access = 1", pid, pname, priv, getDateTime());
		}

		//Using Cupboard priviliges blocked	
		void OnCupboardDeauthorize(BuildingPrivlidge privilege, BasePlayer player) {
		    var priv = privilege.ToString();
		    var pid = player.userID.ToString();
		    var pname = player.displayName.ToString();
		    //PrintWarning(priv+" "+pid+" "+pname);
            executeQuery("INSERT INTO player_authorize_list (player_id, player_name, cupboard, time) VALUES (@0, @1, @2, @3)" +
            			 "ON DUPLICATE KEY UPDATE access = 0", pid, pname, priv, getDateTime());
		}

		//using Cupboard clearing list
		void OnCupboardClearList(BuildingPrivlidge privilege, BasePlayer player) {
		   	var priv = privilege.ToString();
		    var pid = player.userID.ToString();
		    var pname = player.displayName.ToString();
		    //PrintWarning(priv+" "+pid+" "+pname);
		    executeQuery("INSERT INTO player_authorize_list (player_id, player_name, cupboard, time) VALUES (@0, @1, @2, @3)" +
            			 "ON DUPLICATE KEY UPDATE access = 0", pid, pname, priv, getDateTime()," WHERE cupboard=",pid);
		}


        /*********************************
        **      Log Console Stuff       **
        *********************************/

        // log Server commands 
        void OnServerCommand(ConsoleSystem.Arg arg) {
            if (arg.Connection == null) return;
            var command = arg.cmd.FullName;
            var args = arg.GetString(0).ToLower();
            BasePlayer player = (BasePlayer)arg.Connection.player;
            //player use /command
            if (args.StartsWith("/") ) {
            	//PrintWarning( player.userID+" "+player.displayName+" "+command+" "+args+" "+getDateTime() );
            	executeQuery("INSERT INTO player_chat_command (player_id, player_name, command, text, time) VALUES (@0, @1, @2, @3, @4)",
        					 player.userID, EncodeNonAsciiCharacters(player.displayName), command, args, getDateTime() );

            }
            //player ask after admin
            else if (args.Contains("admin") ) {
            	//PrintWarning( player.userID+" "+player.displayName+" "+command+" "+args+" "+getDateTime() );	
				if (LogAdminCall() == true) {
					executeQuery("INSERT INTO admin_log (player_id, player_name, command, text, time) VALUES (@0, @1, @2, @3, @4)",
        						 player.userID, EncodeNonAsciiCharacters(player.displayName), command, args, getDateTime() );
				}
            }
            string words = Config["_AdminLogWords"].ToString();
            string[] word = words.Split(new char[] {' ', ',' ,';','\t','\n', '\r'}, StringSplitOptions.RemoveEmptyEntries);
            foreach (string a in word) {
                //PrintWarning(word);
                if (args.Contains(a) && LogAdminCall() == true ) {
                    //PrintWarning(a);
                    executeQuery("INSERT INTO admin_log (player_id, player_name, command, text, time) VALUES (@0, @1, @2, @3, @4)",
                                 player.userID, EncodeNonAsciiCharacters(player.displayName), command, args, getDateTime() );
                }
            }
        }

        // Log server messages
		void OnServerMessage(string message, string name, string color, ulong id) {
			if (LogConsole() == true) {
				if(name.ToLower()=="server"){
					//PrintWarning(name+" "+message+" "+getDateTime());
					executeQuery("INSERT INTO server_log_console (server_message, time) VALUES (@0, @1)",
	            				 message, getDateTime());
				}
                if(name.Contains("assets")){
                    PrintWarning(message);
                }
			}
		}

        // Log Chat messages
        void OnPlayerChat(ConsoleSystem.Arg arg)  {   
            if (LogChat() == true) {
                BasePlayer player = (BasePlayer)arg.Connection.player;
                var pname = EncodeNonAsciiCharacters(player.displayName);
                var pid = player.userID.ToString();
                var pip = player.net.connection.ipaddress;
                string message = arg.GetString(0);
                if (player.IsAdmin) {
                    // Admin
                    //PrintWarning(pid+" "+pname+" "+pip+" "+message+" 1 "+getDateTime());
                    executeQuery("INSERT INTO server_log_chat (player_id, player_name, player_ip, chat_message, admin, time) VALUES (@0, @1, @2, @3, @4, @5)",
                                 pid, pname, pip, message,"1",getDateTime());
                }
                else {
                    // Player
                    executeQuery("INSERT INTO server_log_chat (player_id, player_name, player_ip, chat_message, admin, time) VALUES (@0, @1, @2, @3, @4, @5)",
                                 pid, pname, pip, message,"0",getDateTime());
                }               
            }
        }

        // log air planes spawned
		void OnAirdrop(CargoPlane plane, Vector3 location) {
			if (LogAiredrop() == true) {
			    string position = location.ToString();
			    string cplane = plane.ToString();
			    //PrintWarning(cplane+" "+position);
			    executeQuery("INSERT INTO server_log_airdrop (plane, location, time) VALUES (@0, @1, @2)",
	            			 cplane, position, getDateTime());
			}
		}


        /*********************************
        **       Console Commands       **
        *********************************/

        // Clear tables
        [ConsoleCommand("mstats.empty")]
        private void EmptyTableCommand(ConsoleSystem.Arg arg) {
            executeQuery("TRUNCATE player_stats");
            executeQuery("TRUNCATE player_resource_gather");
            executeQuery("TRUNCATE player_crafted_item");
            executeQuery("TRUNCATE player_bullets_fired");
            executeQuery("TRUNCATE player_kill_animal");
            executeQuery("TRUNCATE player_kill");
            executeQuery("TRUNCATE player_death");
            executeQuery("TRUNCATE player_destroy_building");
            executeQuery("TRUNCATE player_place_building");
            executeQuery("TRUNCATE player_place_deployable");
            executeQuery("TRUNCATE player_authorize_list");
            executeQuery("TRUNCATE player_chat_command");
            executeQuery("TRUNCATE player_connect_log");
            if (LogChat() == true) {
            	executeQuery("TRUNCATE server_log_chat");
            }
            if (LogConsole() == true) {
            	executeQuery("TRUNCATE server_log_console");
            }
            if (LogAiredrop() == true) {
            	executeQuery("TRUNCATE server_log_airdrop");
            }
            if (LogAdminCall() == true) {
            	executeQuery("TRUNCATE admin_log");
            }          
            PrintWarning("Empty table successful!");
        }

        //Drop tables
        [ConsoleCommand("mstats.drop")]
        private void DropTableCommand(ConsoleSystem.Arg arg) {
            executeQuery("DROP TABLE player_stats");
            executeQuery("DROP TABLE player_resource_gather");
            executeQuery("DROP TABLE player_crafted_item");
            executeQuery("DROP TABLE player_bullets_fired");
            executeQuery("DROP TABLE player_kill_animal");
            executeQuery("DROP TABLE player_kill");
            executeQuery("DROP TABLE player_death");
            executeQuery("DROP TABLE player_destroy_building");
            executeQuery("DROP TABLE player_place_building");
            executeQuery("DROP TABLE player_place_deployable");
            executeQuery("DROP TABLE player_authorize_list");
            executeQuery("DROP TABLE player_chat_command");
            executeQuery("DROP TABLE player_connect_log");
            if (LogChat() == true) {
            	executeQuery("DROP TABLE server_log_chat");
            }
            if (LogConsole() == true) {
            	executeQuery("DROP TABLE server_log_console");
            }
            if (LogAiredrop() == true) {
            	executeQuery("DROP TABLE server_log_airdrop");
            }
            if (LogAdminCall() == true) {
            	executeQuery("DROP TABLE admin_log");
            }           
            PrintWarning("Drop tables successful!");
            PrintWarning("Reload plugin!!");
            //rust.RunServerCommand("oxide.unload MStats");	
        }

        // Reload the plugin
        [ConsoleCommand("mstats.reload")]
        private void ReloadCommand(ConsoleSystem.Arg arg) {
            try {
                PrintWarning("Reloading plugin!");
                rust.RunServerCommand("oxide.reload MStats");
            }
            catch (Exception ex){
                PrintWarning(ex.Message);
            }
        }
        /*********************************
        **          Other Stuff         **
        *********************************/

        bool hasPermission(BasePlayer player, string permissionName) {
            if (player.net.connection.authLevel > 1) return true;
                return permission.UserHasPermission(player.userID.ToString(), permissionName);
        }

        bool usingMySQL() {
            return Convert.ToBoolean(Config["_MySQL"]);
        }

        bool LogChat() {
            return Convert.ToBoolean(Config["_LogChat"]);
        }

        bool LogConsole() {
            return Convert.ToBoolean(Config["_LogConsole"]);
        }

        bool LogAiredrop() {
            return Convert.ToBoolean(Config["_LogAridrops"]);
        }
        
        bool LogAdminCall() {
            return Convert.ToBoolean(Config["_AdminLog"]);
        }

        string GetDistance(BaseCombatEntity victim, BaseEntity attacker) {
            string distance = Convert.ToInt32(Vector3.Distance(victim.transform.position, attacker.transform.position)).ToString();
            return distance;
        }

        string GetFormattedAnimal(string animal) {
        	string[] tokens = animal.Split('[');
            animal = tokens[0].ToUpper();
            return animal;
        }

        string formatBodyPartName(HitInfo hitInfo) {
            string bodypart = "Unknown";
            bodypart = StringPool.Get(Convert.ToUInt32(hitInfo?.HitBone)) ?? "Unknown";
            if ((bool)string.IsNullOrEmpty(bodypart)) bodypart = "Unknown";
            for (int i = 0; i < 10; i++) {
                bodypart = bodypart.Replace(i.ToString(), "");
            }
            bodypart = bodypart.Replace(".prefab", "");
            bodypart = bodypart.Replace("L", "");
            bodypart = bodypart.Replace("R", "");
            bodypart = bodypart.Replace("_", "");
            bodypart = bodypart.Replace(".", "");
            bodypart = bodypart.Replace("right", "");
            bodypart = bodypart.Replace("left", "");
            bodypart = bodypart.Replace("tranform", "");
            bodypart = bodypart.Replace("lowerjaweff", "jaw");
            bodypart = bodypart.Replace("rarmpolevector", "arm");
            bodypart = bodypart.Replace("connection", "");
            bodypart = bodypart.Replace("uppertight", "tight");
            bodypart = bodypart.Replace("fatjiggle", "");
            bodypart = bodypart.Replace("fatend", "");
            bodypart = bodypart.Replace("seff", "");
            bodypart = bodypart.Replace("Unknown", "Bleed to death");
            bodypart = bodypart.ToUpper();
            return bodypart;
        }

 		//Fix names
        static string EncodeNonAsciiCharacters(string value) {
            StringBuilder sb = new StringBuilder();
            foreach (char c in value)
            {
                if (c > 127)
                {
                    // This character is too big for ASCII
                    string encodedValue = "";
                    sb.Append(encodedValue);
                }
                else {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        //Curent day
        private string getDate() {
            return DateTime.Now.ToString("yyyy-MM-dd");
        }

        //Curent time
        private string getDateTime() {
            return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        }

		// get plugin version and database version from config file
		private string getConfigVersion(string value) {
            var curVersions = Convert.ToString(Config["Version"]);
            string[] version = curVersions.Split('.');
            var majorPluginUpdate = version[0];
            var minorPluginUpdate = version[1];           
            var databaseUpdate = version[2];
            if (value == "plugin") {
				return value = majorPluginUpdate+"."+minorPluginUpdate;
            }
            else if  (value == "db"){
            	return value = databaseUpdate;	
            }
            return value = majorPluginUpdate+"."+minorPluginUpdate;
        } 

    }

}