#!/bin/bash -e

${TESTCMD} --label=mcs-tests --timeout=30m make -w -C mcs/tests run-test
${TESTCMD} --label=mcs-errors --timeout=10m make -w -C mcs/errors run-test
