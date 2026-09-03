the time has come to build out our own project management platform.

i want jira/trello lite, self hosted, accessible to you (claude, as an agent in vs code and long term as an independent agent) and me.

we can make this part of the aerie api for now. ultimately we might want to break it off into its own vertical, if that is impactful of any architectural decisions. but primarily it is a project management device for you and me to work on projects and track them outside of juggling md files in this repo.

wants:
- lightweight beautiful gui sdlc management tool
- support kanban process, to begin with all items live on one board and are always visible. we will add filtration and stuff later.
- there is a web app available as its own subdomain on the cloud. it is behind auth wall, requires admin flag.
- claude will need api access for all the project stuff. we'll probably need to build out an api key functionaly or something? you are oyu so you know better, how should we do this?
    - priority-wise, add this functionality to the back end of the implementation. it is valuable to ship a web app that only i, a human, can use, to manage this, in the meantime before you have programmatic access.
    - use-case-wise, i want to be able to paste a link to an issue into a vs code claude chat window, have you read it and get to work
- authz-wise, for the time being, no granular perms. if you can reach the pjm app you are trusted to do whatever.
- use design lib/topbar from the shared chrome
- as a workflow, i would like to be able to pull up a claude prompt in vs code, feed in a ticket number for implementation and have it be done. a planning session should result in info written back to the ticket and perhaps stories/tasks being created
- in the long term i want to look at development/throughput metrics. i don't want reporting as part of the mvp, but i want all work performed to leave audit trails that are reportable and traceable
- the system runs `issue`s through it (open to other domain names if you have opinions) which have various attributes:
    - ID: use jira style IDs, where we can define projects, and tickets get serial ids within that project space.
        - for instance, we might make `AER` the project space for aerie, and the first ticket would have an ID `AER-1`. We might just as well carve off another project for the trading app, make it `TDG` and start making tickets there
        - this does create a requirement for a top-level `project`. create a CRUD page for it.
        - as a whole, the body of `done` tickets in a `project ` defines the entire capability/technical spec of the product
    - type: tells us whether the issue is a feature or bug, or collection of features, or low level task
        - options:
            - `epic` - high-level capability. the main way i interact. i will create an epic, mind-vomit into it, then have you turn it into a series of `stories`
                - an epic may have another epic as its parent. we can organize in any number of higher-order efforts
            - `story` - an atomic piece of functionality. in general stories ship user-observable changes but occasionally they are purely technical.
                - a story has 0 or 1 epics as parents. the parent epic of a story is easily mutable in the ui
                - a story is discretely implementable by Opus or maybe Sonnet
            - `task` - sometimes stories need to track sub-state mid-implementation. tasks are a purely technical level of tracking and don't really matter for business reporting
                - a task is almost always the child of a story
                - a task is discretely implementable by Haiku
            - `bug` - a fix of something that is wrong. not a new feature, but a repair of functionality which is not meeting the established spec
                - bugs may have sub `task`s but it is rare - generally they just need to be investigated and fixed
        - ticket input:
            - based on professional experience i am expecting to be entering tickets with types with statistical spreads of something like [ epi 60%, story 15%, bug 25%]
            - i would expect you to be breaking epics down into stories, sometimes stories into tasks, and creating sub-tickets to implement work i am defining
    - title and description. title is just a text title accepting any string (emojis are happy). description is markdown. when typing it is raw markdown, when rendering it is rich html
    - status: tickets flow through statuses, and statuses are configurable. they handle the full gamut of states that sdlc items can have. for now we will not restrict state transitions and just trust ourselves to move tickets appropriately as an mvp implementation. the statuses allow us to build incremental value over the lifespan of the ticket as it moves across the board. for the most part, one status should have one column in the board.
        - statuses should start out simply with something like:
            - inbox - raw idea from me
            - todo - you have digested an inbox ticket into actionable requirements, or sub-tickets. there are defined acceptance criteria and a user story, and the ticket is testable
            - in progress - you have picked up an atomic item off of the todo list and begun implementation work on it. this includes everything from coding to review, commit/push/deploy. i will move tickets to done.
            - done - we have shipped the issue to prod.
        - statuses are configuration, not code. i can manage statuses in the ui in the tool and add more.
    - attributes are editable in the ui
    - comments - users can leave comments, via ui or api
- integrations - we will want to link github prs and action runs to this platform eventually. not a first order concern but a general direction
- i will want to migrate existing plans into the pjm tool. i want to build a migration tool, and also use this as an opportunity to elucidate the domain concepts:
    - i want to be able to upload the whole `plans` dir into a web app and have it parsed into tickets
    - each md doc is an epic
        - each phase in the doc is a story
        - each checkbox in a story is a task
- we will be moving to a pull request flow where `main` is the protected branch, each unit of work (usually story or bug, occasionally tasks if the story is big) is a branch off of main. once the atomic work is done, the branch is pull-request'd back to main, checks are run, the PR is merged and associated tickets are closed

i want a fun name for the tool, it should be straightforward and obvious but if we can bring it into the aerie theme a little i'd love it. the web app should be reachable on `{name}.${domain}` and visible in the app picker. it should have a simple icon.

ultimately i will want to build this into a fuller featured beautiful tool, but for the time being i want to start managing projects in a real pjm tool that i control rather than a markdown editor! so we are going to be mvp hawks here.
