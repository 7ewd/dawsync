package com.maltese.bitwig;

import java.util.UUID;

import com.bitwig.extension.api.PlatformType;
import com.bitwig.extension.controller.AutoDetectionMidiPortNamesList;
import com.bitwig.extension.controller.ControllerExtension;
import com.bitwig.extension.controller.ControllerExtensionDefinition;
import com.bitwig.extension.controller.api.ControllerHost;

/** Bitwig の「コントローラー」として追加すると、同じ PC の Maltese アプリとつながる。 */
public class BridgeDefinition extends ControllerExtensionDefinition {
    static final String VERSION = "0.6.2";

    @Override public String getName() { return "Maltese"; }
    @Override public String getAuthor() { return "Maltese"; }
    @Override public String getVersion() { return VERSION; }
    @Override public UUID getId() { return UUID.fromString("5f0e6a52-8f7e-4c2c-9d3b-6a1d2f7c4e11"); }
    @Override public int getRequiredAPIVersion() { return 18; }
    @Override public String getHardwareVendor() { return "Maltese"; }
    @Override public String getHardwareModel() { return "Maltese"; }
    @Override public int getNumMidiInPorts() { return 0; }
    @Override public int getNumMidiOutPorts() { return 0; }
    @Override public void listAutoDetectionMidiPortNames(AutoDetectionMidiPortNamesList list, PlatformType platform) { }
    @Override public ControllerExtension createInstance(ControllerHost host) { return new BridgeExtension(this, host); }
}
