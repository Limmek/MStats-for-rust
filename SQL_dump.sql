-- phpMyAdmin SQL Dump
-- version 4.5.5.1
-- http://www.phpmyadmin.net
--
-- Host: 127.0.0.1:3306
-- Generation Time: Jul 20, 2016 at 04:42 PM
-- Server version: 5.6.29
-- PHP Version: 7.0.4

SET SQL_MODE = "NO_AUTO_VALUE_ON_ZERO";
SET time_zone = "+00:00";

--
-- Database: `mstats`
--

-- --------------------------------------------------------

--
-- Table structure for table `player_authorize_list`
--

CREATE TABLE `player_authorize_list` (
  `id` int(11) NOT NULL,
  `player_id` bigint(20) DEFAULT NULL,
  `player_name` varchar(128) DEFAULT NULL,
  `cupboard` varchar(128) DEFAULT NULL,
  `access` int(32) DEFAULT '0',
  `time` timestamp NULL DEFAULT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8;

-- --------------------------------------------------------

--
-- Table structure for table `player_bullets_fired`
--

CREATE TABLE `player_bullets_fired` (
  `id` int(11) NOT NULL,
  `player_id` bigint(20) DEFAULT NULL,
  `bullet_name` varchar(128) DEFAULT NULL,
  `bullets_fired` int(32) DEFAULT '1',
  `weapon_name` varchar(128) DEFAULT NULL,
  `date` date DEFAULT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8;

-- --------------------------------------------------------

--
-- Table structure for table `player_call_admin`
--

CREATE TABLE `player_call_admin` (
  `id` int(11) NOT NULL,
  `player_id` bigint(20) DEFAULT NULL,
  `player_name` varchar(128) DEFAULT NULL,
  `command` varchar(128) DEFAULT NULL,
  `text` varchar(255) DEFAULT NULL,
  `time` timestamp NULL DEFAULT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8;

-- --------------------------------------------------------

--
-- Table structure for table `player_chat_command`
--

CREATE TABLE `player_chat_command` (
  `id` int(11) NOT NULL,
  `player_id` bigint(20) DEFAULT NULL,
  `player_name` varchar(128) DEFAULT NULL,
  `command` varchar(128) DEFAULT NULL,
  `text` varchar(255) DEFAULT NULL,
  `time` timestamp NULL DEFAULT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8;

-- --------------------------------------------------------

--
-- Table structure for table `player_connect_log`
--

CREATE TABLE `player_connect_log` (
  `id` int(11) NOT NULL,
  `player_id` bigint(20) DEFAULT NULL,
  `player_name` varchar(128) DEFAULT NULL,
  `state` varchar(128) DEFAULT NULL,
  `time` timestamp NULL DEFAULT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8;

-- --------------------------------------------------------

--
-- Table structure for table `player_crafted_item`
--

CREATE TABLE `player_crafted_item` (
  `id` int(11) NOT NULL,
  `player_id` bigint(20) DEFAULT NULL,
  `item` varchar(128) DEFAULT NULL,
  `amount` int(32) DEFAULT NULL,
  `date` date DEFAULT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8;

-- --------------------------------------------------------

--
-- Table structure for table `player_death`
--

CREATE TABLE `player_death` (
  `id` int(11) NOT NULL,
  `player_id` bigint(20) DEFAULT NULL,
  `cause` varchar(128) DEFAULT NULL,
  `count` int(11) DEFAULT '1',
  `date` date DEFAULT NULL,
  `time` timestamp NULL DEFAULT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8;

-- --------------------------------------------------------

--
-- Table structure for table `player_destroy_building`
--

CREATE TABLE `player_destroy_building` (
  `id` int(11) NOT NULL,
  `player_id` bigint(20) DEFAULT NULL,
  `building` varchar(128) DEFAULT NULL,
  `building_grade` varchar(128) DEFAULT NULL,
  `weapon` varchar(128) DEFAULT NULL,
  `time` timestamp NULL DEFAULT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8;

-- --------------------------------------------------------

--
-- Table structure for table `player_kill`
--

CREATE TABLE `player_kill` (
  `id` int(11) NOT NULL,
  `killer_id` bigint(20) DEFAULT NULL,
  `victim_id` bigint(20) DEFAULT NULL,
  `bodypart` varchar(128) DEFAULT NULL,
  `weapon` varchar(128) DEFAULT NULL,
  `distance` int(11) DEFAULT NULL,
  `time` timestamp NULL DEFAULT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8;

-- --------------------------------------------------------

--
-- Table structure for table `player_kill_animal`
--

CREATE TABLE `player_kill_animal` (
  `id` int(11) NOT NULL,
  `player_id` bigint(20) DEFAULT NULL,
  `animal` varchar(128) DEFAULT NULL,
  `distance` int(11) DEFAULT '0',
  `weapon` varchar(128) DEFAULT NULL,
  `time` timestamp NULL DEFAULT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8;

-- --------------------------------------------------------

--
-- Table structure for table `player_place_building`
--

CREATE TABLE `player_place_building` (
  `id` int(11) NOT NULL,
  `player_id` bigint(20) DEFAULT NULL,
  `player_name` varchar(128) DEFAULT NULL,
  `building` varchar(128) DEFAULT NULL,
  `amount` int(32) DEFAULT '1',
  `date` date DEFAULT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8;

-- --------------------------------------------------------

--
-- Table structure for table `player_place_deployable`
--

CREATE TABLE `player_place_deployable` (
  `id` int(11) NOT NULL,
  `player_id` bigint(20) DEFAULT NULL,
  `player_name` varchar(128) DEFAULT NULL,
  `deployable` varchar(128) DEFAULT NULL,
  `amount` int(32) DEFAULT '1',
  `date` date DEFAULT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8;

-- --------------------------------------------------------

--
-- Table structure for table `player_resource_gather`
--

CREATE TABLE `player_resource_gather` (
  `id` int(11) NOT NULL,
  `player_id` bigint(20) DEFAULT NULL,
  `player_name` varchar(255) DEFAULT NULL,
  `resource` varchar(255) DEFAULT NULL,
  `amount` int(32) DEFAULT NULL,
  `date` date DEFAULT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8;

-- --------------------------------------------------------

--
-- Table structure for table `player_stats`
--

CREATE TABLE `player_stats` (
  `id` int(11) NOT NULL,
  `player_id` bigint(20) DEFAULT NULL,
  `player_name` varchar(255) DEFAULT NULL,
  `player_ip` varchar(128) DEFAULT NULL,
  `player_state` int(1) DEFAULT '0',
  `player_online_time` bigint(20) DEFAULT '0',
  `player_last_login` timestamp NULL DEFAULT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8;

-- --------------------------------------------------------

--
-- Table structure for table `player_xp`
--

CREATE TABLE `player_xp` (
  `id` int(11) NOT NULL,
  `player_id` bigint(20) NOT NULL DEFAULT '0',
  `player_xp_total` float DEFAULT '0',
  `player_xp_spent` float DEFAULT '0'
) ENGINE=InnoDB DEFAULT CHARSET=utf8;

-- --------------------------------------------------------

--
-- Table structure for table `server_log_airdrop`
--

CREATE TABLE `server_log_airdrop` (
  `id` int(11) NOT NULL,
  `plane` varchar(128) DEFAULT NULL,
  `location` varchar(128) DEFAULT NULL,
  `time` timestamp NULL DEFAULT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8;

-- --------------------------------------------------------

--
-- Table structure for table `server_log_chat`
--

CREATE TABLE `server_log_chat` (
  `id` int(11) NOT NULL,
  `player_id` bigint(20) DEFAULT NULL,
  `player_name` varchar(128) DEFAULT NULL,
  `player_ip` varchar(128) DEFAULT NULL,
  `chat_message` varchar(255) DEFAULT NULL,
  `admin` int(32) DEFAULT '0',
  `time` timestamp NULL DEFAULT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8;

-- --------------------------------------------------------

--
-- Table structure for table `server_log_console`
--

CREATE TABLE `server_log_console` (
  `id` int(11) NOT NULL,
  `server_message` varchar(255) DEFAULT NULL,
  `time` timestamp NULL DEFAULT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8;

--
-- Indexes for dumped tables
--

--
-- Indexes for table `player_authorize_list`
--
ALTER TABLE `player_authorize_list`
  ADD PRIMARY KEY (`id`),
  ADD UNIQUE KEY `cupboard` (`cupboard`);

--
-- Indexes for table `player_bullets_fired`
--
ALTER TABLE `player_bullets_fired`
  ADD PRIMARY KEY (`id`),
  ADD UNIQUE KEY `PlayerBullet` (`player_id`,`bullet_name`,`weapon_name`,`date`);

--
-- Indexes for table `player_call_admin`
--
ALTER TABLE `player_call_admin`
  ADD PRIMARY KEY (`id`);

--
-- Indexes for table `player_chat_command`
--
ALTER TABLE `player_chat_command`
  ADD PRIMARY KEY (`id`);

--
-- Indexes for table `player_connect_log`
--
ALTER TABLE `player_connect_log`
  ADD PRIMARY KEY (`id`);

--
-- Indexes for table `player_crafted_item`
--
ALTER TABLE `player_crafted_item`
  ADD PRIMARY KEY (`id`),
  ADD UNIQUE KEY `PlayerItem` (`player_id`,`item`,`date`);

--
-- Indexes for table `player_death`
--
ALTER TABLE `player_death`
  ADD PRIMARY KEY (`id`),
  ADD UNIQUE KEY `player_id` (`player_id`,`date`,`cause`);

--
-- Indexes for table `player_destroy_building`
--
ALTER TABLE `player_destroy_building`
  ADD PRIMARY KEY (`id`);

--
-- Indexes for table `player_kill`
--
ALTER TABLE `player_kill`
  ADD PRIMARY KEY (`id`);

--
-- Indexes for table `player_kill_animal`
--
ALTER TABLE `player_kill_animal`
  ADD PRIMARY KEY (`id`);

--
-- Indexes for table `player_place_building`
--
ALTER TABLE `player_place_building`
  ADD PRIMARY KEY (`id`),
  ADD UNIQUE KEY `player_id` (`player_id`,`date`,`building`);

--
-- Indexes for table `player_place_deployable`
--
ALTER TABLE `player_place_deployable`
  ADD PRIMARY KEY (`id`),
  ADD UNIQUE KEY `player_id` (`player_id`,`date`,`deployable`);

--
-- Indexes for table `player_resource_gather`
--
ALTER TABLE `player_resource_gather`
  ADD PRIMARY KEY (`id`),
  ADD UNIQUE KEY `PlayerGather` (`player_id`,`resource`,`date`);

--
-- Indexes for table `player_stats`
--
ALTER TABLE `player_stats`
  ADD PRIMARY KEY (`id`),
  ADD UNIQUE KEY `player_id` (`player_id`);

--
-- Indexes for table `player_xp`
--
ALTER TABLE `player_xp`
  ADD PRIMARY KEY (`player_id`),
  ADD UNIQUE KEY `id` (`id`);

--
-- Indexes for table `server_log_airdrop`
--
ALTER TABLE `server_log_airdrop`
  ADD PRIMARY KEY (`id`);

--
-- Indexes for table `server_log_chat`
--
ALTER TABLE `server_log_chat`
  ADD PRIMARY KEY (`id`);

--
-- Indexes for table `server_log_console`
--
ALTER TABLE `server_log_console`
  ADD PRIMARY KEY (`id`);

--
-- AUTO_INCREMENT for dumped tables
--

--
-- AUTO_INCREMENT for table `player_authorize_list`
--
ALTER TABLE `player_authorize_list`
  MODIFY `id` int(11) NOT NULL AUTO_INCREMENT;
--
-- AUTO_INCREMENT for table `player_bullets_fired`
--
ALTER TABLE `player_bullets_fired`
  MODIFY `id` int(11) NOT NULL AUTO_INCREMENT;
--
-- AUTO_INCREMENT for table `player_call_admin`
--
ALTER TABLE `player_call_admin`
  MODIFY `id` int(11) NOT NULL AUTO_INCREMENT;
--
-- AUTO_INCREMENT for table `player_chat_command`
--
ALTER TABLE `player_chat_command`
  MODIFY `id` int(11) NOT NULL AUTO_INCREMENT;
--
-- AUTO_INCREMENT for table `player_connect_log`
--
ALTER TABLE `player_connect_log`
  MODIFY `id` int(11) NOT NULL AUTO_INCREMENT, AUTO_INCREMENT=60;
--
-- AUTO_INCREMENT for table `player_crafted_item`
--
ALTER TABLE `player_crafted_item`
  MODIFY `id` int(11) NOT NULL AUTO_INCREMENT;
--
-- AUTO_INCREMENT for table `player_death`
--
ALTER TABLE `player_death`
  MODIFY `id` int(11) NOT NULL AUTO_INCREMENT;
--
-- AUTO_INCREMENT for table `player_destroy_building`
--
ALTER TABLE `player_destroy_building`
  MODIFY `id` int(11) NOT NULL AUTO_INCREMENT;
--
-- AUTO_INCREMENT for table `player_kill`
--
ALTER TABLE `player_kill`
  MODIFY `id` int(11) NOT NULL AUTO_INCREMENT;
--
-- AUTO_INCREMENT for table `player_kill_animal`
--
ALTER TABLE `player_kill_animal`
  MODIFY `id` int(11) NOT NULL AUTO_INCREMENT;
--
-- AUTO_INCREMENT for table `player_place_building`
--
ALTER TABLE `player_place_building`
  MODIFY `id` int(11) NOT NULL AUTO_INCREMENT;
--
-- AUTO_INCREMENT for table `player_place_deployable`
--
ALTER TABLE `player_place_deployable`
  MODIFY `id` int(11) NOT NULL AUTO_INCREMENT;
--
-- AUTO_INCREMENT for table `player_resource_gather`
--
ALTER TABLE `player_resource_gather`
  MODIFY `id` int(11) NOT NULL AUTO_INCREMENT, AUTO_INCREMENT=253;
--
-- AUTO_INCREMENT for table `player_stats`
--
ALTER TABLE `player_stats`
  MODIFY `id` int(11) NOT NULL AUTO_INCREMENT, AUTO_INCREMENT=31;
--
-- AUTO_INCREMENT for table `player_xp`
--
ALTER TABLE `player_xp`
  MODIFY `id` int(11) NOT NULL AUTO_INCREMENT, AUTO_INCREMENT=27;
--
-- AUTO_INCREMENT for table `server_log_airdrop`
--
ALTER TABLE `server_log_airdrop`
  MODIFY `id` int(11) NOT NULL AUTO_INCREMENT, AUTO_INCREMENT=3;
--
-- AUTO_INCREMENT for table `server_log_chat`
--
ALTER TABLE `server_log_chat`
  MODIFY `id` int(11) NOT NULL AUTO_INCREMENT, AUTO_INCREMENT=2;
--
-- AUTO_INCREMENT for table `server_log_console`
--
ALTER TABLE `server_log_console`
  MODIFY `id` int(11) NOT NULL AUTO_INCREMENT;