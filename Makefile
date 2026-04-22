
COVERAGE_RAW    = build/coverage-raw
COVERAGE_REPORT = build/coverage-report
PACKAGE_ID      = QiWa.framework
PACKAGE_VERSION ?= 0.2.1
PACKAGE_OUTPUT  = bin/Release
BUILD_CONFIGURATION ?= Debug
NUGET_PACK_ARGS ?=

# 对应“运行 make pack 并没有生成 nuget 文件，请解决”：这些命令目标不能和同名目录冲突，否则 make 会跳过真正的构建与打包动作。
.PHONY: build new test coverage pack push

# make build BUILD_CONFIGURATION=release
build:
	# 对应“运行 make pack 并没有生成 nuget 文件，请解决”：逐个目标框架构建，绕开当前多目标 dotnet build 不收尾的问题。
	dotnet build QiWa.framework.csproj -c $(BUILD_CONFIGURATION) -f net8.0 --verbosity minimal
	dotnet build QiWa.framework.csproj -c $(BUILD_CONFIGURATION) -f net10.0 --verbosity minimal

new:
	dotnet new sln -n "QiWa.framework"

test:
	dotnet test Tests/QiWa.framework.Tests/QiWa.framework.Tests.csproj --verbosity minimal

fmt:
	dotnet format QiWa.framework.csproj

coverage:
	rm -rf $(COVERAGE_RAW) $(COVERAGE_REPORT)
	dotnet test Tests/QiWa.framework.Tests/QiWa.framework.Tests.csproj \
		--verbosity minimal \
		--collect:"XPlat Code Coverage" \
		--results-directory $(COVERAGE_RAW)
	which reportgenerator > /dev/null 2>&1 || \
		dotnet tool install --global dotnet-reportgenerator-globaltool
	reportgenerator \
		"-reports:$(COVERAGE_RAW)/**/coverage.cobertura.xml" \
		"-targetdir:$(COVERAGE_REPORT)" \
		"-reporttypes:Html"
	@echo ""
	@echo "Coverage report: $(COVERAGE_REPORT)/index.html"

pack:
	# 对应“运行 make pack 并没有生成 nuget 文件，请解决”：改为先构建 Release，再用显式 nuspec 稳定生成 NuGet 包。
	$(MAKE) build BUILD_CONFIGURATION=Release
	nuget pack $(PACKAGE_ID).nuspec \
		-Version $(PACKAGE_VERSION) \
		-OutputDirectory $(PACKAGE_OUTPUT) \
		-NoPackageAnalysis \
		$(NUGET_PACK_ARGS)

# make push KEY=$(cat app_key.txt)
push:
	dotnet nuget push $(PACKAGE_OUTPUT)/$(PACKAGE_ID).$(PACKAGE_VERSION).nupkg \
		--api-key $(KEY) \
		--source https://api.nuget.org/v3/index.json
