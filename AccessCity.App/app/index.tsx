import React, { useState } from "react";
import {
  View,
  Text,
  TextInput,
  TouchableOpacity,
  StyleSheet,
  Image,
  ScrollView,
  SafeAreaView,
} from "react-native";
import { LinearGradient } from "expo-linear-gradient";
import { AntDesign, FontAwesome, Ionicons } from "@expo/vector-icons";

export default function AuthScreen() {
  const [isSignup, setIsSignup] = useState(false);

  return (
    <SafeAreaView style={styles.safeArea}>
      <ScrollView
        contentContainerStyle={styles.scrollContent}
        showsVerticalScrollIndicator={false}
      >
        <View style={styles.container}>
          <View style={styles.topShape} />

          <Image
            source={require("../assets/images/logo.png")}
            style={styles.logo}
            resizeMode="contain"
          />

          <Text style={styles.title}>
            Navigate Your City <Text style={styles.safetyText}>Safely</Text>
          </Text>

          <Text style={styles.subtitle}>
            Find safer walking routes and stay aware of hazards around you.
          </Text>

          <View style={styles.switchContainer}>
            <TouchableOpacity
              style={[styles.tabButton, !isSignup && styles.activeTab]}
              onPress={() => setIsSignup(false)}
            >
              <Text style={[styles.tabText, !isSignup && styles.activeTabText]}>
                Log In
              </Text>
            </TouchableOpacity>

            <TouchableOpacity
              style={[styles.tabButton, isSignup && styles.activeTab]}
              onPress={() => setIsSignup(true)}
            >
              <Text style={[styles.tabText, isSignup && styles.activeTabText]}>
                Sign up
              </Text>
            </TouchableOpacity>
          </View>

          <View style={styles.dividerRow}>
            <View style={styles.divider} />
            <Text style={styles.dividerText}>
              {isSignup ? "Sign up with" : "Login with"}
            </Text>
            <View style={styles.divider} />
          </View>

          <View style={styles.socialRow}>
            <TouchableOpacity style={styles.socialButton}>
              <AntDesign name="google" size={18} color="#EA4335" />
              <Text style={styles.socialText}>Google</Text>
            </TouchableOpacity>

            <TouchableOpacity style={styles.socialButton}>
              <FontAwesome name="facebook" size={18} color="#1877F2" />
              <Text style={styles.socialText}>Facebook</Text>
            </TouchableOpacity>

            <TouchableOpacity style={styles.socialButton}>
              <AntDesign name="apple1" size={18} color="#000" />
              <Text style={styles.socialText}>Apple</Text>
            </TouchableOpacity>
          </View>

          <View style={styles.dividerRow}>
            <View style={styles.divider} />
            <Text style={styles.dividerText}>Or</Text>
            <View style={styles.divider} />
          </View>

          {isSignup ? (
            <>
              <View style={styles.nameRow}>
                <View style={styles.halfWidth}>
                  <View style={styles.fieldWrapBlue}>
                    <Text style={styles.floatingLabelBlue}>First Name</Text>
                    <TextInput
                      placeholder="Enter first name"
                      placeholderTextColor="#C7C7C7"
                      style={styles.inputBlue}
                    />
                  </View>
                </View>

                <View style={styles.halfWidth}>
                  <View style={styles.fieldWrapBlue}>
                    <Text style={styles.floatingLabelBlue}>Last Name</Text>
                    <TextInput
                      placeholder="Enter last name"
                      placeholderTextColor="#C7C7C7"
                      style={styles.inputBlue}
                    />
                  </View>
                </View>
              </View>

              <View style={styles.fieldWrap}>
                <Text style={styles.floatingLabel}>Email</Text>
                <TextInput
                  placeholder="Please enter your email"
                  placeholderTextColor="#C7C7C7"
                  style={styles.input}
                />
              </View>

              <View style={styles.fieldWrap}>
                <Text style={styles.floatingLabel}>Set Password</Text>
                <View style={styles.passwordRow}>
                  <TextInput
                    placeholder="**********"
                    placeholderTextColor="#C7C7C7"
                    secureTextEntry
                    style={styles.passwordInput}
                  />
                  <Ionicons name="eye-outline" size={20} color="#B7B7B7" />
                </View>
              </View>

              <View style={styles.fieldWrap}>
                <Text style={styles.floatingLabel}>Confirm Password</Text>
                <View style={styles.passwordRow}>
                  <TextInput
                    placeholder="**********"
                    placeholderTextColor="#C7C7C7"
                    secureTextEntry
                    style={styles.passwordInput}
                  />
                  <Ionicons name="eye-outline" size={20} color="#B7B7B7" />
                </View>
              </View>

              <TouchableOpacity activeOpacity={0.9} style={styles.buttonOuter}>
                <LinearGradient
                  colors={["#2E6AB0", "#215A9A"]}
                  start={{ x: 0, y: 0 }}
                  end={{ x: 1, y: 0 }}
                  style={styles.mainButton}
                >
                  <Text style={styles.mainButtonText}>Sign up</Text>
                </LinearGradient>
              </TouchableOpacity>
            </>
          ) : (
            <>
              <View style={styles.fieldWrap}>
                <Text style={styles.floatingLabelBlue}>Email</Text>
                <TextInput
                  placeholder="Please enter your email"
                  placeholderTextColor="#C7C7C7"
                  style={styles.input}
                />
              </View>

              <View style={styles.fieldWrap}>
                <Text style={styles.floatingLabel}>Password</Text>
                <View style={styles.passwordRow}>
                  <TextInput
                    placeholder="**********"
                    placeholderTextColor="#C7C7C7"
                    secureTextEntry
                    style={styles.passwordInput}
                  />
                  <Ionicons name="eye-outline" size={20} color="#B7B7B7" />
                </View>
              </View>

              <TouchableOpacity>
                <Text style={styles.forgotPassword}>Forgot Password?</Text>
              </TouchableOpacity>

              <TouchableOpacity activeOpacity={0.9} style={styles.buttonOuter}>
                <LinearGradient
                  colors={["#2E6AB0", "#215A9A"]}
                  start={{ x: 0, y: 0 }}
                  end={{ x: 1, y: 0 }}
                  style={styles.mainButton}
                >
                  <Text style={styles.mainButtonText}>Log In</Text>
                </LinearGradient>
              </TouchableOpacity>
            </>
          )}
        </View>
      </ScrollView>
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  safeArea: {
    flex: 1,
    backgroundColor: "#F7F7F7",
  },
  scrollContent: {
    flexGrow: 1,
  },
  container: {
    flex: 1,
    backgroundColor: "#F7F7F7",
    paddingHorizontal: 20,
    paddingTop: 10,
    paddingBottom: 35,
  },
  topShape: {
    position: "absolute",
    top: 0,
    left: 0,
    width: 180,
    height: 180,
    backgroundColor: "#F1E2D7",
    borderBottomRightRadius: 180,
  },
  logo: {
    width: 130,
    height: 130,
    alignSelf: "center",
    marginTop: 28,
    marginBottom: 8,
    backgroundColor: "transparent",
  },
  title: {
    fontSize: 22,
    fontWeight: "800",
    textAlign: "center",
    color: "#4B3D3D",
    marginBottom: 8,
  },
  safetyText: {
    color: "#F59B23",
    textShadowColor: "rgba(33,90,154,0.25)",
    textShadowOffset: { width: 0, height: 1 },
    textShadowRadius: 6,
  },
  subtitle: {
    textAlign: "center",
    color: "#5D5D5D",
    fontSize: 15,
    lineHeight: 22,
    marginBottom: 24,
    paddingHorizontal: 8,
  },
  switchContainer: {
    flexDirection: "row",
    backgroundColor: "#ECECF1",
    borderRadius: 10,
    padding: 4,
    marginBottom: 24,
  },
  tabButton: {
    flex: 1,
    paddingVertical: 13,
    alignItems: "center",
    borderRadius: 10,
  },
  activeTab: {
    backgroundColor: "#FFFFFF",
  },
  tabText: {
    color: "#7B7B8B",
    fontSize: 16,
    fontWeight: "500",
  },
  activeTabText: {
    color: "#1C2452",
    fontWeight: "700",
  },
  dividerRow: {
    flexDirection: "row",
    alignItems: "center",
    marginBottom: 18,
  },
  divider: {
    flex: 1,
    height: 1,
    backgroundColor: "#DADADA",
  },
  dividerText: {
    marginHorizontal: 12,
    color: "#7B7B8B",
    fontSize: 16,
    fontWeight: "600",
  },
  socialRow: {
    flexDirection: "row",
    justifyContent: "space-between",
    gap: 10,
    marginBottom: 22,
  },
  socialButton: {
    flex: 1,
    backgroundColor: "#FFFFFF",
    borderWidth: 1,
    borderColor: "#E2E2E2",
    borderRadius: 12,
    paddingVertical: 12,
    alignItems: "center",
    justifyContent: "center",
    minHeight: 50,
  },
  socialText: {
    marginTop: 6,
    fontSize: 14,
    fontWeight: "600",
    color: "#111111",
  },
  nameRow: {
    flexDirection: "row",
    gap: 12,
  },
  halfWidth: {
    flex: 1,
  },
  fieldWrap: {
    position: "relative",
    marginBottom: 18,
  },
  fieldWrapBlue: {
    position: "relative",
    marginBottom: 18,
  },
  floatingLabel: {
    position: "absolute",
    top: -9,
    left: 14,
    zIndex: 2,
    backgroundColor: "#F7F7F7",
    paddingHorizontal: 6,
    color: "#9A9A9A",
    fontSize: 14,
    fontWeight: "600",
  },
  floatingLabelBlue: {
    position: "absolute",
    top: -9,
    left: 14,
    zIndex: 2,
    backgroundColor: "#F7F7F7",
    paddingHorizontal: 6,
    color: "#114B9B",
    fontSize: 14,
    fontWeight: "700",
  },
  input: {
    backgroundColor: "#FFFFFF",
    borderWidth: 1.5,
    borderColor: "#D9D9D9",
    borderRadius: 12,
    paddingHorizontal: 16,
    paddingVertical: 16,
    minHeight: 54,
    color: "#1F2937",
  },
  inputBlue: {
    backgroundColor: "#FFFFFF",
    borderWidth: 1.5,
    borderColor: "#2E6AB0",
    borderRadius: 12,
    paddingHorizontal: 16,
    paddingVertical: 16,
    minHeight: 54,
    color: "#1F2937",
  },
  passwordRow: {
    backgroundColor: "#FFFFFF",
    borderWidth: 1.5,
    borderColor: "#D9D9D9",
    borderRadius: 12,
    paddingHorizontal: 16,
    minHeight: 54,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
  },
  passwordInput: {
    flex: 1,
    color: "#1F2937",
    paddingVertical: 16,
  },
  forgotPassword: {
    textAlign: "right",
    color: "#114B9B",
    fontWeight: "700",
    marginBottom: 22,
  },
  buttonOuter: {
    borderRadius: 14,
    shadowColor: "#114B9B",
    shadowOpacity: 0.25,
    shadowRadius: 8,
    shadowOffset: { width: 0, height: 4 },
    elevation: 5,
  },
  mainButton: {
    borderRadius: 14,
    minHeight: 56,
    justifyContent: "center",
    alignItems: "center",
  },
  mainButtonText: {
    color: "#FFFFFF",
    fontSize: 18,
    fontWeight: "700",
  },
});