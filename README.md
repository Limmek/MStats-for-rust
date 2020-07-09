# MySql Stats Log for Rust
Rust plugin that logs player and server statistics to MySql.

Plugin creates database tables if not exists. NO SQL file needed!

------
###How to use and install the plugin:
1. Download and put MStats in plugn folder.
2. Edit the config file (MStats.json).
3. Reload the plugin.

------
### Plugin options
All options is set to FALSE as default!<br>
* TurncateDataOnMonthlyWipe - Clear database on rust force wipe. (*Default* - **10 minutes**)
* TurncateDataOnMapWipe - Clear database on map change. (*Default* - **false**)
* _LogAridrops  - Logs cargo planes spawned.
* _AdminLog - Logs if player call for admin.
* _LogChat      - Logs Global chat.
* _LogConsole   - Logs server messages/commands, etc admin spawn item.
* _MySQL        - Enable this for plugin to work.
* _AdminLogWords - Specific words writen by players to be loged.
* _LogServerCargoship - Logs cargoships spawned.
* _LogServerPatrolhelicopter - Logs Patrol helicopters spawned.
* _LogServerbradleyapc - Logs bradleys spawned.
* _LogServerch47 - Logs ch47 spawned.

------
###Console Commands
After Drop Table plugin must be reloaded for a new tables to be created!<br>
* **mstats.reload**  - Reload the plugin.
* **mstats.empty**  - Truncate tables. Clear all tables from data.
* **mstats.drop**   - Drops all tables. Removes all table from database.

------
###Version info!
**1.0.1** = **x.x.d**<br>
**x.x** = Plugin version **1.0** <br>
**d** = Database version **1**<br>
The database version is for the plugin to respond on update if an update on tables is made and might need to be removed.

------
If you have any problems with the plugin please leave a comment or send me a message!
Keep in mind this is my second plugin!
