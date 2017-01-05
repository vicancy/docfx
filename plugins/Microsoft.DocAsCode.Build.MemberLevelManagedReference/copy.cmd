@ECHO OFF
PUSHD %1
ECHO COPYING Files into plugins
IF NOT EXIST plugins mkdir plugins

copy ..\*.dll plugins

POPD