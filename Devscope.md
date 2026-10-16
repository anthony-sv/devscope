# Devscope

## Requirements

1. We have to make sure we have 2 things:
   1. Feature 1: We put 2 ear files, we have to tell the diff of the files of each 2.
   2. Feature 2: We want to put a route which can be a folder containing multiple log files, or a log file, or a log file and m ultiple folders where insider could be 1 or multiple logs, and we would love to check stuff around that log, like filtering, the tail, putting some colors, etc, kind of like seq for centralized structured logging but the logs could not be structured you know.

## Context

This is what I have in mind, but of course, you can propose a couple of things in each feature, first I thought of doing it in java swing or java fx, or something in c# and make it distributable, because we would like to put the ear file in a location, could be a network drive path, and a local one, not uploading it, because of course could be terrible for the code to fly on the web you know, but maybe you know some work arounds? or what should we do?
I guess is the same thing with the logs:

we could specify:

- A log file in a network drive
- A path with any structure of folder and logs in a network drive
- A local log file
- A local path with any structure of folder an logs in a network drive

and thats per app...

Lets say we have the logs of another app, and we want to open every log of that app in every env, like in a workspace.

We have:

- Local path with multiple subfolder containging log each one and a main log.
- Then the app goes into another env and thats a network drive with same structure
- Then the app goes into beta, beta is a cluster so we have 2 paths with the same structure
- THen the app goes into prd, prod is a cluster so we have another 2 paths but could live in the same server

lets say we have under D:/logs/app, //server1:/logs/app, //server2:/logs/beta/app, //server3:/logs/beta/app, //server2:/logs/prod/app, and //server3:/logs/prod/app

and same thing would be better if those would not leave the network you know.
