Let's analyze the code of the application (C# console application) to get to know what the app does, it contains the source code for a console app, some models and a couple of utilities. But first analyze everything. After you have analyzed the code, check the files @README.md, @SPEC.md and @DECISIONS.md. They contain the purpose of the application, how it must work, the process that needs to be followed and the requirements to be met. I have installed globally openspec, you can use the commands, if you have any questions look on the following URL how to use it (https://openspec.dev/).

Before you start, let's create the spec, everything must be clear, if you have any questions let me know. If we can't resolve the questions you may have, we need to create a commercial debt md file in order to get the answers from the user.

The interaction is:

0. You read the code, the provided files (README, SPEC and DECISIONS files), review them with me, if you have further questions we check them, if there are doubts after this we create the DEBTS.md file with all the unsanswered question
1. you create the specs for each part of the coding phase, try to create as many micro tasks as you can, we save all the tasks in a PROGRESS.md file, so we can mark them in each iteration 
2. We review everything and I approve
3. We can start coding by looking on the PROGRESS.md file, once you create the code for any task we review them, I test and debug, once the requirement has been satisfied we can mark the task done and UPDATE the progress file
4. Create the required test for each task and of course the e2e test to be able to check the whole progress and to determine if we are missing something globally

# Critical rules:

* Don't change any code, first I review and approve
* If you have questions we can try to answer them, if we are not able to do it, we put it on the TECH_DEBT.md file
* We need to follow good coding conventions, best coding practices and of course good architecture.
* Save the memory on each iteration so we can track every step