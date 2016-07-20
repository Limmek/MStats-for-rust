# MySql Chat Log for Rust
Rust plugin that logs player and server statistics to MySql.

Plugin creates database tables if not exists. NO SQL file needed!


------
###How to use and install the plugin:
1. Download and put MStats in plugn folder.
2. Edit the config file (MStats.json).
3. Reload the plugin.

------

### Plugin options
Default false
* _LogAridrops  - Logs cargo planes spawned.
* _AdminLog - Logs if player call for admin.
* _LogChat      - Logs Global chat.
* _LogConsole   - Logs server messages/commands, etc admin spawn item.
* _MySQL        - Enable this for plugin to work.
* _AdminLogWords - Spesific words writen by players to be loged.
------
###Console Commands
* **mstats.empty**  - Truncate tables. Clear all tables from data.
* **mstats.drop**   - Drops all tables. Removes all table from database.
After Drop Table plugin must be reloaded for a new tables to be created!

------
###TO-DO List:
* Web template.
* Truncate/Drop one table only.

------
If you have any problems with the plugin please leave a comment or send me a message!
Keep in mind this is my second plugin!
